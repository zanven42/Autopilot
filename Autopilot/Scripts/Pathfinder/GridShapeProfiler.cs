﻿using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Collections;
using VRage.ModAPI;
using VRageMath;

namespace Rynchodon.Autopilot.Pathfinder
{
	/// <summary>
	/// <para>Creates a List of every occupied cell for a grid. This List is used to create projections of the grid.</para>
	/// <para>This class only ever uses local positions.</para>
	/// </summary>
	/// TODO: Contains() based comparison of rejected cells
	internal class GridShapeProfiler
	{

		#region GridCellCache
		
		/// <summary>
		/// Keeps a HashSet of all the occupied cells in a grid.
		/// </summary>
		private class GridCellCache
		{

			private static Dictionary<IMyCubeGrid, GridCellCache> CellCache = new Dictionary<IMyCubeGrid, GridCellCache>();
			private static FastResourceLock lock_cellCache = new FastResourceLock();

			public static GridCellCache GetCellCache(IMyCubeGrid grid)
			{
				GridCellCache cache;
				using (lock_cellCache.AcquireExclusiveUsing())
					if (!CellCache.TryGetValue(grid, out cache))
					{
						cache = new GridCellCache(grid);
						CellCache.Add(grid, cache);
					}
				return cache;
			}

			public HashSet<Vector3I> CellPositions = new HashSet<Vector3I>();
			public readonly FastResourceLock lock_cellPositions = new FastResourceLock();

			public GridCellCache(IMyCubeGrid grid)
			{
				List<IMySlimBlock> dummy = new List<IMySlimBlock>();
				MainLock.UsingShared(() => {
					using (lock_cellPositions.AcquireExclusiveUsing())
						grid.GetBlocks(dummy, slim => {
							slim.ForEachCell(cell => {
								CellPositions.Add(cell);
								return false;
							});
							return false;
						});

					grid.OnBlockAdded += grid_OnBlockAdded;
					grid.OnBlockRemoved += grid_OnBlockRemoved;
					grid.OnClosing += grid_OnClosing;
				});
			}

			private void grid_OnClosing(IMyEntity obj)
			{
				IMyCubeGrid grid = obj as IMyCubeGrid;
				grid.OnBlockAdded -= grid_OnBlockAdded;
				grid.OnBlockRemoved -= grid_OnBlockRemoved;
				grid.OnClosing -= grid_OnClosing;
				using (lock_cellPositions.AcquireExclusiveUsing())
					CellPositions = null;
				using (lock_cellCache.AcquireExclusiveUsing())
					CellCache.Remove(grid);
			}

			private void grid_OnBlockAdded(IMySlimBlock slim)
			{
				using (lock_cellPositions.AcquireExclusiveUsing())
					slim.ForEachCell(cell => {
						CellPositions.Add(cell);
						return false;
					});
			}

			private void grid_OnBlockRemoved(IMySlimBlock slim)
			{
				using (lock_cellPositions.AcquireExclusiveUsing())
					slim.ForEachCell(cell => {
						CellPositions.Remove(cell);
						return false;
					});
			}

		}

		#endregion

		private Logger m_logger = new Logger(null, "GridShapeProfiler");
		private IMyCubeGrid m_grid;
		private GridCellCache m_cellCache;
		private Vector3 m_centreRejection;
		private Vector3 m_directNorm;
		private readonly MyUniqueList<Vector3> m_rejectionCells = new MyUniqueList<Vector3>();

		public Capsule Path { get; private set; }

		private Vector3 Centre { get { return m_grid.LocalAABB.Center; } }

		public GridShapeProfiler() { }

		public void Init(IMyCubeGrid grid, RelativePosition3F destination, Vector3 navBlockLocalPosition)
		{
			if (grid != m_grid)
			{
				this.m_grid = grid;
				this.m_logger = new Logger("GridShapeProfiler", () => m_grid.DisplayName);
				this.m_cellCache = GridCellCache.GetCellCache(grid);
			}

			m_directNorm = Vector3.Normalize(destination.ToLocal() - navBlockLocalPosition);
			Vector3 centreDestination = destination.ToLocal() + Centre - navBlockLocalPosition;
			rejectAll();
			createCapsule(centreDestination, navBlockLocalPosition);
		}

		/// <summary>
		/// Rejection test for intersection with the profiled grid.
		/// </summary>
		/// <param name="grid">Grid whose cells will be rejected and compared to the profiled grid's rejections.</param>
		/// <returns>True iff there is a collision</returns>
		public bool rejectionIntersects(IMyCubeGrid grid, IMyCubeBlock ignore, out MyEntity entity, out Vector3? pointOfObstruction)
		{
			m_logger.debugLog(m_grid == null, "m_grid == null", "Init()", Logger.severity.FATAL);

			m_logger.debugLog("testing grid: " + grid.getBestName(), "rejectionIntersects()");

			GridCellCache gridCache = GridCellCache.GetCellCache(grid);
			MatrixD toLocal = m_grid.WorldMatrixNormalizedInv;
			Line pathLine = Path.get_Line();

			using (gridCache.lock_cellPositions.AcquireSharedUsing())
				foreach (Vector3I cell in gridCache.CellPositions)
				{
					Vector3 world = grid.GridIntegerToWorld(cell);
					if (pathLine.PointInCylinder(Path.Radius, world))
					{
						Vector3 local = Vector3.Transform(world, toLocal);
						if (rejectionIntersects(local, grid.GridSize))
						{
							entity = grid.GetCubeBlock(cell).FatBlock as MyEntity ?? grid as MyEntity;
							if (ignore != null && entity == ignore)
								continue;

							pointOfObstruction = Path.get_Line().ClosestPoint(world);
							return true;
						}
					}
				}

			entity = null;
			pointOfObstruction = null;
			return false;
		}

		/// <summary>
		/// Rejection test for intersection with the profiled grid.
		/// </summary>
		/// <param name="localMetresPosition">The local position in metres.</param>
		/// <returns>true if the rejection collides with one or more of the grid's rejections</returns>
		private bool rejectionIntersects(Vector3 localMetresPosition, float GridSize)
		{
			Vector3 TestRejection = RejectMetres(localMetresPosition);
			foreach (Vector3 ProfileRejection in m_rejectionCells)
				if (Vector3.DistanceSquared(TestRejection, ProfileRejection) < 2 * GridSize + 2 * m_grid.GridSize)
					return true;
			return false;
		}

		private Vector3 RejectMetres(Vector3 metresPosition)
		{ return Vector3.Reject(metresPosition, m_directNorm); }

		/// <summary>
		/// Perform a vector rejection of every occupied cell from DirectionNorm and store the results in rejectionCells.
		/// </summary>
		private void rejectAll()
		{
			m_rejectionCells.Clear();

			m_centreRejection = RejectMetres(Centre);
			using (m_cellCache.lock_cellPositions.AcquireSharedUsing())
				foreach (Vector3I cell in m_cellCache.CellPositions)
				{
					Vector3 rejection = RejectMetres(cell * m_grid.GridSize);
					m_rejectionCells.Add(rejection);
				}
		}

		/// <param name="centreDestination">where the centre of the grid will end up (local)</param>
		private void createCapsule(Vector3 centreDestination, Vector3 localPosition)
		{
			float longestDistanceSquared = 0;
			foreach (Vector3 rejection in m_rejectionCells)
			{
				float distanceSquared = (rejection - m_centreRejection).LengthSquared();
				if (distanceSquared > longestDistanceSquared)
					longestDistanceSquared = distanceSquared;
			}
			Vector3D P0 = RelativePosition3F.FromLocal(m_grid, Centre).ToWorld();

			//// need to extend capsule past destination by distance between remote and front of ship
			//Ray navTowardsDest = new Ray(localPosition, m_directNorm);
			//float tMin, tMax;
			//m_grid.LocalVolume.IntersectRaySphere(navTowardsDest, out tMin, out tMax);
			//Vector3D P1 = RelativeVector3F.createFromLocal(centreDestination + tMax * m_directNorm, m_grid).getWorldAbsolute();
			Vector3D P1 = RelativePosition3F.FromLocal(m_grid, centreDestination).ToWorld();

			float CapsuleRadius = (float)(Math.Pow(longestDistanceSquared, 0.5) + 3 * m_grid.GridSize);
			Path = new Capsule(P0, P1, CapsuleRadius);
		}

	}
}

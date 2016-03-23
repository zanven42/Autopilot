using System;
using Rynchodon.Autopilot.Data;
using Rynchodon.Autopilot.Navigator;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace Rynchodon.Autopilot.Movement
{
	/// <summary>
	/// Performs the movement calculations and the movements.
	/// </summary>
	public class Mover
	{
		#region S.E. Constants

		private const float pixelsForMaxRotation = 20;
		private const float RollControlMultiplier = 0.2f;

		#endregion

		/// <summary>Fraction of force used to calculate maximum speed.</summary>
		public const float AvailableForceRatio = 0.5f;

		/// <summary>Only update torque accel ratio when updates are at least this close together.</summary>
		private const float MaxUpdateSeconds = Globals.UpdatesPerSecond;
		private const ulong StuckAfter = 1000;

		/// <summary>Controlling block for the grid.</summary>
		public readonly ShipControllerBlock Block;
		public ThrustProfiler myThrust;

		private readonly Logger myLogger;
		private readonly AllNavigationSettings NavSet;

		private IMyCubeGrid myGrid;
		private GyroProfiler myGyro;

		private Vector3 moveForceRatio = Vector3.Zero;
		private Vector3 rotateForceRatio = Vector3.Zero;
		private Vector3 m_moveAccel;

		private Vector3 prevAngleVel = Vector3.Zero;
		private DateTime updated_prevAngleVel; // needs to be precise, regardless of updates

		private float best_distance = float.MaxValue, best_angle = float.MaxValue;
		private ulong m_stuckAt = ulong.MaxValue;

		private bool m_stopped, m_overworked;

		public Pathfinder.Pathfinder myPathfinder { get; private set; }
		public Vector3 WorldGravity { get { return myThrust.m_worldGravity; } }

		/// <summary>Value is false iff this Mover is making progress.</summary>
		public bool IsStuck
		{
			get
			{
				//myLogger.debugLog("distance: " + NavSet.Settings_Current.Distance + ", best: " + best_distance + ", angle: " + NavSet.Settings_Current.DistanceAngle + ", best: " + best_angle +
				//	", update: " + Globals.UpdateCount + ", stuck at: " + m_stuckAt, "get_IsStuck()");
				return Globals.UpdateCount >= m_stuckAt;
			}
			set
			{
				if (value)
					m_stuckAt = 0ul;
				else
					m_stuckAt = Globals.UpdateCount + StuckAfter;
			}
		}

		/// <summary>
		/// Creates a Mover for a given ShipControllerBlock and AllNavigationSettings
		/// </summary>
		/// <param name="block">Controlling block for the grid</param>
		/// <param name="NavSet">Navigation settings to use.</param>
		public Mover(ShipControllerBlock block, AllNavigationSettings NavSet)
		{
			this.myLogger = new Logger("Mover", block.Controller);
			this.Block = block;
			this.NavSet = NavSet;

			CheckGrid();
		}

		/// <summary>
		/// Sets moveForceRatio to zero, optionally enables damping.
		/// </summary>
		public void StopMove(bool enableDampeners = true)
		{
			moveForceRatio = Vector3.Zero;
			m_moveAccel = Vector3.Zero;
			Block.SetDamping(enableDampeners);
		}

		/// <summary>
		/// Sets rotateForceRatio to zero.
		/// </summary>
		public void StopRotate()
		{
			rotateForceRatio = Vector3.Zero;
		}

		public void MoveAndRotateStop()
		{
			if (m_stopped)
				return;

			StopMove();
			StopRotate();
			MyAPIGateway.Utilities.TryInvokeOnGameThread(() => Block.Controller.MoveAndRotateStopped());
			m_stopped = true;
		}

		/// <summary>
		/// Calculates whether or not the grid has sufficient thrust to accelerate forwards at 1 m/s/s.
		/// </summary>
		/// <returns>True iff there is sufficient thrust available.</returns>
		public bool CanMoveForward()
		{
			CheckGrid();

			return myThrust.GetForceInDirection(Block.CubeBlock.Orientation.Forward, true) > Block.Physics.Mass;
		}

		/// <summary>
		/// Calculates the force necessary to move the grid.
		/// </summary>
		/// <param name="block">To get world position from.</param>
		/// <param name="destPoint">The world position of the destination</param>
		/// <param name="destVelocity">The speed of the destination</param>
		/// <param name="landing">Puts an emphasis on not overshooting the target.</param>
		[Obsolete("Navigators should use double precision")]
		public void CalcMove(PseudoBlock block, Vector3 destPoint, Vector3 destVelocity, bool landing = false)
		{
			CalcMove(block, (Vector3D)destPoint, destVelocity, landing);
		}

		/// <summary>
		/// Calculates the force necessary to move the grid.
		/// </summary>
		/// <param name="block">To get world position from.</param>
		/// <param name="destPoint">The world position of the destination</param>
		/// <param name="destVelocity">The speed of the destination</param>
		/// <param name="landing">Puts an emphasis on not overshooting the target.</param>
		public void CalcMove(PseudoBlock block, Vector3D destPoint, Vector3 destVelocity, bool landing = false)
		{
			CheckGrid();

			// using world vectors

			if (NavSet.Settings_Current.CollisionAvoidance)
			{
				myPathfinder.TestPath(destPoint, landing);
				if (!myPathfinder.CanMove)
				{
					myLogger.debugLog("Pathfinder not allowing movement", "CalcMove()");
					return;
				}
			}

			myThrust.Update();

			Vector3 destDisp = destPoint - block.WorldPosition; // this is why we need double for destPoint
			Vector3 velocity = Block.CubeGrid.Physics.LinearVelocity;

			// switch to using local vectors

			MatrixD positionToLocal = Block.CubeBlock.WorldMatrixNormalizedInv;
			MatrixD directionToLocal = positionToLocal.GetOrientation();

			destVelocity = Vector3.Transform(destVelocity, directionToLocal);
			velocity = Vector3.Transform(velocity, directionToLocal);

			Vector3 targetVelocity;
			float distance;
			if (destDisp != Vector3.Zero)
			{
				destDisp = Vector3.Transform(destDisp, directionToLocal);
				distance = destDisp.Length();

				if (distance + 2f < best_distance || float.IsNaN(NavSet.Settings_Current.Distance))
				{
					best_distance = distance;
					m_stuckAt = Globals.UpdateCount + StuckAfter;
				}

				targetVelocity = MaximumVelocity(destDisp);

				// project targetVelocity onto destination direction (take shortest path)
				Vector3 destDir = destDisp / distance;
				targetVelocity = Vector3.Dot(targetVelocity, destDir) * destDir;

				// apply relative speed limit
				float relSpeedLimit = NavSet.Settings_Current.SpeedMaxRelative;
				if (landing)
				{
					float landingSpeed = Math.Max(distance * 0.2f, 0.2f);
					if (relSpeedLimit > landingSpeed)
						relSpeedLimit = landingSpeed;
				}
				if (relSpeedLimit < float.MaxValue)
				{
					float tarSpeedSq_1 = targetVelocity.LengthSquared();
					if (tarSpeedSq_1 > relSpeedLimit * relSpeedLimit)
					{
						targetVelocity *= relSpeedLimit / (float)Math.Sqrt(tarSpeedSq_1);
						myLogger.debugLog("imposing relative speed limit: " + relSpeedLimit + ", targetVelocity: " + targetVelocity, "CalcMove()");
					}
				}
			}
			else
			{
				targetVelocity = Vector3.Zero;
				distance = 0f;
				m_stuckAt = Globals.UpdateCount + StuckAfter;
			}

			NavSet.Settings_Task_NavWay.Distance = distance;
			targetVelocity += destVelocity;

			// apply speed limit
			float tarSpeedSq = targetVelocity.LengthSquared();
			float speedRequest = NavSet.Settings_Current.SpeedTarget;
			if (tarSpeedSq > speedRequest * speedRequest)
			{
				targetVelocity *= speedRequest / (float)Math.Sqrt(tarSpeedSq);
				myLogger.debugLog("imposing speed limit: " + speedRequest + ", targetVelocity: " + targetVelocity, "CalcMove()");
			}
			else if (tarSpeedSq < velocity.LengthSquared())
				NavSet.Settings_Task_NavWay.NearingDestination = true;

			m_moveAccel = targetVelocity - velocity - myThrust.m_localGravity;

			moveForceRatio = ToForceRatio(m_moveAccel);

			// dampeners
			bool enableDampeners = false;

			for (int i = 0; i < 3; i++)
			{
				//const float minForceRatio = 0.1f;
				//const float zeroForceRatio = 0.01f;

				float forceRatio = moveForceRatio.GetDim(i);
				if (forceRatio < 1f && forceRatio > -1f)
				{
					//if (forceRatio > zeroForceRatio && forceRatio < minForceRatio)
					//	moveForceRatio.SetDim(i, minForceRatio);
					//else if (forceRatio < -zeroForceRatio && forceRatio > -minForceRatio)
					//	moveForceRatio.SetDim(i, -minForceRatio);
					continue;
				}

				// force ratio is > 1 || < -1. If it is useful, use dampeners

				float velDim = velocity.GetDim(i);
				if (velDim < 1f && velDim > -1f)
					continue;

				if (Math.Sign(forceRatio) * Math.Sign(velDim) < 0)
				{
					myLogger.debugLog("damping, i: " + i + ", force ratio: " + forceRatio + ", velocity: " + velDim + ", sign of forceRatio: " + Math.Sign(forceRatio) + ", sign of velocity: " + Math.Sign(velDim), "CalcMove()");
					moveForceRatio.SetDim(i, 0);
					enableDampeners = true;
				}
				//else
				//	myLogger.debugLog("not damping, i: " + i + ", force ratio: " + forceRatio + ", velocity: " + velDim + ", sign of forceRatio: " + Math.Sign(forceRatio) + ", sign of velocity: " + Math.Sign(velDim), "CalcMove()");
			}

			Block.SetDamping(enableDampeners);

			myLogger.debugLog(string.Empty
				//+ "block: " + block.Block.getBestName()
				//+ ", dest point: " + destPoint
				//+ ", position: " + block.WorldPosition
				+ "destDisp: " + destDisp
				+ ", destVelocity: " + destVelocity
				+ ", targetVelocity: " + targetVelocity
				+ ", velocity: " + velocity
				+ ", m_moveAccel: " + m_moveAccel
				+ ", moveForceRatio: " + moveForceRatio, "CalcMove()");
		}

		/// <summary>
		/// Calculates the maximum velocity that will allow the grid to stop at the destination.
		/// </summary>
		/// <param name="localDisp">The displacement to the destination.</param>
		/// <returns>The maximum velocity that will allow the grid to stop at the destination.</returns>
		private Vector3 MaximumVelocity(Vector3 localDisp)
		{
			Vector3 result = Vector3.Zero;

			if (localDisp.X > 0f)
				result.X = MaximumSpeed(localDisp.X, Base6Directions.Direction.Left);
			else if (localDisp.X < 0f)
				result.X = -MaximumSpeed(-localDisp.X, Base6Directions.Direction.Right);
			if (localDisp.Y > 0f)
				result.Y = MaximumSpeed(localDisp.Y, Base6Directions.Direction.Down);
			else if (localDisp.Y < 0f)
				result.Y = -MaximumSpeed(-localDisp.Y, Base6Directions.Direction.Up);
			if (localDisp.Z > 0f)
				result.Z = MaximumSpeed(localDisp.Z, Base6Directions.Direction.Forward);
			else if (localDisp.Z < 0f)
				result.Z = -MaximumSpeed(-localDisp.Z, Base6Directions.Direction.Backward);

			myLogger.debugLog("displacement: " + localDisp + ", maximum velocity: " + result, "MaximumVelocity()");

			return result;
		}

		/// <summary>
		/// Calculates the maximum speed that will allow the grid to stop at the destination.
		/// </summary>
		/// <param name="dist">The distance to the destination</param>
		/// <param name="direct">The directional thrusters to use</param>
		/// <returns>The maximum speed that will allow the grid to stop at the destination.</returns>
		private float MaximumSpeed(float dist, Base6Directions.Direction direct)
		{
			if (dist < 0.01f)
				return 0f;

			direct = Block.CubeBlock.Orientation.TransformDirection(direct);
			float force = myThrust.GetForceInDirection(direct, true) * AvailableForceRatio;
			if (force < 1f)
			{
				myLogger.debugLog("No thrust available in direction: " + direct + ", dist: " + dist, "MaximumSpeed()", Logger.severity.DEBUG);
				return dist * 0.1f;
			}
			float accel = -force / Block.Physics.Mass;
			myLogger.debugLog("direction: " + direct + ", dist: " + dist + ", max accel: " + accel + ", mass: " + Block.Physics.Mass + ", max speed: " + PrettySI.makePretty(Math.Sqrt(-2f * accel * dist)) + " m/s" + ", cap: " + dist * 0.5f + " m/s", "MaximumSpeed()");
			return Math.Min((float)Math.Sqrt(-2f * accel * dist), dist * 0.5f); // capped for the sake of autopilot's reaction time
		}

		/// <summary>
		/// Calculate the force ratio from acceleration.
		/// </summary>
		/// <param name="localAccel">Acceleration</param>
		/// <returns>Force ratio</returns>
		private Vector3 ToForceRatio(Vector3 localAccel)
		{
			Vector3 result = Vector3.Zero;

			if (localAccel.X > 0f)
				result.X = localAccel.X * Block.Physics.Mass / myThrust.GetForceInDirection(Base6Directions.GetFlippedDirection(Block.CubeBlock.Orientation.Left));
			else if (localAccel.X < 0f)
				result.X = localAccel.X * Block.Physics.Mass / myThrust.GetForceInDirection(Block.CubeBlock.Orientation.Left);
			if (localAccel.Y > 0f)
				result.Y = localAccel.Y * Block.Physics.Mass / myThrust.GetForceInDirection(Block.CubeBlock.Orientation.Up);
			else if (localAccel.Y < 0f)
				result.Y = localAccel.Y * Block.Physics.Mass / myThrust.GetForceInDirection(Base6Directions.GetFlippedDirection(Block.CubeBlock.Orientation.Up));
			if (localAccel.Z > 0f)
				result.Z = localAccel.Z * Block.Physics.Mass / myThrust.GetForceInDirection(Base6Directions.GetFlippedDirection(Block.CubeBlock.Orientation.Forward));
			else if (localAccel.Z < 0f)
				result.Z = localAccel.Z * Block.Physics.Mass / myThrust.GetForceInDirection(Block.CubeBlock.Orientation.Forward);

			myLogger.debugLog("accel: " + localAccel + ", force ratio: " + result + ", mass: " + Block.Physics.Mass, "ToForceRatio()");

			return result;
		}

		/// <summary>
		/// If the ship is in gravity, calculates the roll to level off.
		/// </summary>
		/// <returns>True iff the ship is in gravity.</returns>
		private bool InGravity_LevelOff()
		{
			// TODO: use myThrust.Standard

			if (myThrust.m_localGravity == Vector3.Zero)
				return false;

			myLogger.debugLog("Rotating to fight gravity", "InGravity_LevelOff()", Logger.severity.TRACE);
			//Logger.notify("Rotating to fight gravity", 50);
			Matrix localMatrix = Block.CubeBlock.LocalMatrix;
			localMatrix.Forward = Block.CubeBlock.LocalMatrix.Up;
			localMatrix.Up = Block.CubeBlock.LocalMatrix.Backward;
			RelativeDirection3F Direction = RelativeDirection3F.FromLocal(Block.CubeGrid, -myThrust.m_localGravity);
			RelativeDirection3F UpDirect = null;

			CalcRotate(localMatrix, Direction, UpDirect, true);
			return true;
		}

		/// <summary>
		/// Rotate to face the best direction for flight.
		/// </summary>
		public void CalcRotate()
		{
			if (InGravity_LevelOff())
				return;

			if (Block.Physics.LinearVelocity.LengthSquared() < 100f)
			{
				myLogger.debugLog("speed low, rotate to accel, LinearVelocity: " + Block.Physics.LinearVelocity, "CalcRotate()");
				CalcRotate(myThrust.Standard, RelativeDirection3F.FromBlock(Block.CubeBlock, m_moveAccel));
				return;
			}

			if (NavSet.Settings_Current.NearingDestination && NavSet.Settings_Current.Distance > 10f) // orbiter needs to be exempt
			{
				myLogger.debugLog("rotate to stop", "CalcRotate()");
				CalcRotate(myThrust.Standard, RelativeDirection3F.FromWorld(Block.CubeGrid, -Block.Physics.LinearVelocity));
				return;
			}

			if (m_moveAccel.LengthSquared() > 100f)
			{
				myLogger.debugLog("accel high, rotate to accel, m_moveAccel: " + m_moveAccel, "CalcRotate()");
				CalcRotate(myThrust.Standard, RelativeDirection3F.FromBlock(Block.CubeBlock, m_moveAccel));
				return;
			}

			myLogger.debugLog("stopping rotation", "CalcRotate()");
			StopRotate();
		}

		/// <summary>
		/// Calculate the best rotation to stop the ship.
		/// </summary>
		public void CalcRotate_Stop()
		{
			if (InGravity_LevelOff())
				return; 
			
			myLogger.debugLog("rotate to stop", "CalcRotate_Stop()");
			CalcRotate(myThrust.Standard, RelativeDirection3F.FromWorld(Block.CubeGrid, -Block.Physics.LinearVelocity));
		}

		/// <summary>
		/// Match orientation with the target block.
		/// </summary>
		/// <param name="block">The navigation block</param>
		/// <param name="destBlock">The destination block</param>
		/// <param name="forward">The direction of destBlock that will be matched to navigation block's forward</param>
		/// <param name="upward">The direction of destBlock that will be matched to navigation block's upward</param>
		/// <returns>True iff localMatrix is facing the same direction as destBlock's forward</returns>
		public void CalcRotate(PseudoBlock block, IMyCubeBlock destBlock, Base6Directions.Direction? forward, Base6Directions.Direction? upward)
		{
			if (forward == null)
				forward = Base6Directions.Direction.Forward;

			RelativeDirection3F faceForward = RelativeDirection3F.FromWorld(block.Grid, destBlock.WorldMatrix.GetDirectionVector(forward.Value));
			RelativeDirection3F faceUpward = upward.HasValue ? RelativeDirection3F.FromWorld(block.Grid, destBlock.WorldMatrix.GetDirectionVector(upward.Value)) : null;

			CalcRotate(block.LocalMatrix, faceForward, faceUpward);
		}

		/// <summary>
		/// Calculates the force necessary to rotate the grid.
		/// </summary>
		/// <param name="Direction">The direction to face the localMatrix in.</param>
		/// <param name="block"></param>
		/// <returns>True iff localMatrix is facing Direction</returns>
		public void CalcRotate(PseudoBlock block, RelativeDirection3F Direction, RelativeDirection3F UpDirect = null, IMyEntity targetEntity = null)
		{
			CalcRotate(block.LocalMatrix, Direction, UpDirect, targetEntity: targetEntity);
		}

		// necessary wrapper for main CalcRotate, should always be called.
		private void CalcRotate(Matrix localMatrix, RelativeDirection3F Direction, RelativeDirection3F UpDirect, bool levelingOff = false, IMyEntity targetEntity = null)
		{
			CheckGrid();
			myThrust.Update();

			if (!levelingOff)
			{
				if (ThrustersOverWorked())
				{
					myLogger.debugLog("Thrusters overworked; overruling navigator, need to level off", "CalcRotate()");
					if (InGravity_LevelOff())
						return;
					else
						myLogger.alwaysLog("Failed gravity check!", "CalcRotate()", Logger.severity.FATAL);
				}
				if (NavSet.Settings_Current.CollisionAvoidance && !myPathfinder.CanMove && InGravity_LevelOff())
				{
					myLogger.debugLog("Pathfinder preventing movement; level off", "CalcRotate()");
					return;
				}
			}

			Vector3 angleVelocity;
			CalcRotate(localMatrix, Direction, UpDirect, out angleVelocity, targetEntity);
			prevAngleVel = angleVelocity;
		}

		/// <summary>
		/// Calculates the force necessary to rotate the grid.
		/// </summary>
		/// <param name="localMatrix">The matrix to rotate to face the direction, use a block's local matrix or result of GetMatrix()</param>
		/// <param name="Direction">The direction to face the localMatrix in.</param>
		/// <param name="angularVelocity">The local angular velocity of the controlling block.</param>
		private void CalcRotate(Matrix localMatrix, RelativeDirection3F Direction, RelativeDirection3F UpDirect, out Vector3 angularVelocity, IMyEntity targetEntity)
		{
			myLogger.debugLog(Direction == null, "Direction == null", "CalcRotate()", Logger.severity.ERROR);

			angularVelocity = -Vector3.Transform(Block.Physics.AngularVelocity, Block.CubeBlock.WorldMatrixNormalizedInv.GetOrientation());

			//myLogger.debugLog("angular: " + angularVelocity, "CalcRotate()");
			float gyroForce = myGyro.TotalGyroForce();

			float secondsSinceLast = (float)(DateTime.UtcNow - updated_prevAngleVel).TotalSeconds;
			updated_prevAngleVel = DateTime.UtcNow;

			if (rotateForceRatio != Vector3.Zero)
			{
				if (secondsSinceLast <= MaxUpdateSeconds)
				{
					Vector3 ratio = (angularVelocity - prevAngleVel) / (rotateForceRatio * gyroForce * secondsSinceLast);

					//myLogger.debugLog("rotateForceRatio: " + rotateForceRatio + ", ratio: " + ratio + ", accel: " + (angularVelocity - prevAngleVel) + ", torque: " + (rotateForceRatio * gyroForce), "CalcRotate()");

					myGyro.Update_torqueAccelRatio(rotateForceRatio, ratio);
				}
				else
					myLogger.debugLog("prevAngleVel is old: " + secondsSinceLast, "CalcRotate()", Logger.severity.DEBUG);
			}

			localMatrix.M41 = 0; localMatrix.M42 = 0; localMatrix.M43 = 0; localMatrix.M44 = 1;
			Matrix inverted; Matrix.Invert(ref localMatrix, out inverted);

			localMatrix = localMatrix.GetOrientation();
			inverted = inverted.GetOrientation();

			//myLogger.debugLog("local matrix: right: " + localMatrix.Right + ", up: " + localMatrix.Up + ", back: " + localMatrix.Backward + ", trans: " + localMatrix.Translation, "CalcRotate()");
			//myLogger.debugLog("inverted matrix: right: " + inverted.Right + ", up: " + inverted.Up + ", back: " + inverted.Backward + ", trans: " + inverted.Translation, "CalcRotate()");
			//myLogger.debugLog("local matrix: " + localMatrix, "CalcRotate()");
			//myLogger.debugLog("inverted matrix: " + inverted, "CalcRotate()");

			Vector3 localDirect = Direction.ToLocal();
			Vector3 rotBlockDirect; Vector3.Transform(ref localDirect, ref inverted, out rotBlockDirect);

			rotBlockDirect.Normalize();

			float azimuth, elevation; Vector3.GetAzimuthAndElevation(rotBlockDirect, out azimuth, out elevation);

			Vector3 rotaRight = localMatrix.Right;
			Vector3 rotaUp = localMatrix.Up;

			Vector3 NFR_right = Base6Directions.GetVector(Block.CubeBlock.LocalMatrix.GetClosestDirection(ref rotaRight));
			Vector3 NFR_up = Base6Directions.GetVector(Block.CubeBlock.LocalMatrix.GetClosestDirection(ref rotaUp));

			Vector3 displacement = -elevation * NFR_right - azimuth * NFR_up;

			if (UpDirect != null)
			{
				Vector3 upLocal = UpDirect.ToLocal();
				Vector3 upRotBlock; Vector3.Transform(ref upLocal, ref inverted, out upRotBlock);
				float roll; Vector3.Dot(ref upRotBlock, ref Vector3.Right, out roll);

				Vector3 rotaBackward = localMatrix.Backward;
				Vector3 NFR_backward = Base6Directions.GetVector(Block.CubeBlock.LocalMatrix.GetClosestDirection(ref rotaBackward));

				myLogger.debugLog("roll: " + roll + ", displacement: " + displacement + ", NFR_backward: " + NFR_backward + ", change: " + (roll * NFR_backward), "CalcRotate()");

				displacement += roll * NFR_backward;
			}

			if (NavSet.Settings_Current.CollisionAvoidance)
			{
				myPathfinder.TestRotate(displacement);
				if (!myPathfinder.CanRotate)
				{
					myLogger.debugLog("Pathfinder not allowing rotation", "CalcRotate()");
					return;
				}
			}

			float distanceAngle = displacement.Length();
			if (distanceAngle < best_angle || float.IsNaN(NavSet.Settings_Current.DistanceAngle))
			{
				best_angle = distanceAngle;
				m_stuckAt = Globals.UpdateCount + StuckAfter;
			}
			NavSet.Settings_Task_NavWay.DistanceAngle = distanceAngle;

			myLogger.debugLog("localDirect: " + localDirect + ", rotBlockDirect: " + rotBlockDirect + ", elevation: " + elevation + ", NFR_right: " + NFR_right + ", azimuth: " + azimuth + ", NFR_up: " + NFR_up + ", disp: " + displacement, "CalcRotate()");

			if (myGyro.torqueAccelRatio == 0)
			{
				// do a test
				myLogger.debugLog("torqueAccelRatio == 0", "CalcRotate()");
				rotateForceRatio = new Vector3(0, 1f, 0);
				return;
			}

			Vector3 targetVelocity = MaxAngleVelocity(displacement, secondsSinceLast, targetEntity != null);

			//float timeToReachVelocity;
			// adjustment to face a moving entity
			if (targetEntity != null)
			{
				Vector3 relativeLinearVelocity = targetEntity.GetLinearVelocity() - Block.Physics.LinearVelocity - Block.Physics.LinearAcceleration * 0.1f;
				float distance = Vector3.Distance(targetEntity.GetCentre(), Block.CubeBlock.GetPosition());

				//myLogger.debugLog("relativeLinearVelocity: " + relativeLinearVelocity + ", tangentialVelocity: " + tangentialVelocity + ", localTangVel: " + localTangVel, "CalcRotate()");

				float RLV_pitch = Vector3.Dot(relativeLinearVelocity, Block.CubeBlock.WorldMatrix.Down);
				float RLV_yaw = Vector3.Dot(relativeLinearVelocity, Block.CubeBlock.WorldMatrix.Right);
				float angl_pitch = (float)Math.Atan2(RLV_pitch, distance);
				float angl_yaw = (float)Math.Atan2(RLV_yaw, distance);

				myLogger.debugLog("relativeLinearVelocity: " + relativeLinearVelocity + ", RLV_yaw: " + RLV_yaw + ", RLV_pitch: " + RLV_pitch + ", angl_yaw: " + angl_yaw + ", angl_pitch: " + angl_pitch, "CalcRotate()");

				targetVelocity += new Vector3(angl_pitch, angl_yaw, 0f);
			}

			rotateForceRatio = (targetVelocity - angularVelocity) / (myGyro.torqueAccelRatio * gyroForce * 0.1f);

			myLogger.debugLog("targetVelocity: " + targetVelocity + ", angularVelocity: " + angularVelocity + ", accel: " + (targetVelocity - angularVelocity), "CalcRotate()");
			myLogger.debugLog("accel: " + (targetVelocity - angularVelocity) + ", torque: " + (myGyro.torqueAccelRatio * gyroForce) + ", rotateForceRatio: " + rotateForceRatio, "CalcRotate()");

			// dampeners
			for (int i = 0; i < 3; i++)
			{
				// if targetVelocity is close to 0, use dampeners

				float target = targetVelocity.GetDim(i);
				if (target > -0.01f && target < 0.01f)
				{
					//myLogger.debugLog("target near 0 for " + i + ", " + target, "CalcRotate()");
					rotateForceRatio.SetDim(i, 0f);
					continue;
				}
			}
		}

		/// <summary>
		/// Calulates the maximum angular velocity to stop at the destination.
		/// </summary>
		/// <param name="disp">Displacement to the destination.</param>
		/// <returns>The maximum angular velocity to stop at the destination.</returns>
		private Vector3 MaxAngleVelocity(Vector3 disp, float secondsSinceLast, bool fast)
		{
			Vector3 result = Vector3.Zero;

			// S.E. provides damping for angular motion, we will ignore this
			float accel = -myGyro.torqueAccelRatio * myGyro.TotalGyroForce();

			//myLogger.debugLog("torqueAccelRatio: " + myGyro.torqueAccelRatio + ", TotalGyroForce: " + myGyro.TotalGyroForce() + ", accel: " + accel, "MaxAngleVelocity()");

			for (int i = 0; i < 3; i++)
			{
				float dim = disp.GetDim(i);
				if (dim > 0)
					result.SetDim(i, MaxAngleSpeed(accel, dim, fast));
				else if (dim < 0)
					result.SetDim(i, -MaxAngleSpeed(accel, -dim, fast));
			}

			return result;
		}

		/// <summary>
		/// Calculates the maximum angular speed to stop at the destination.
		/// </summary>
		/// <param name="accel">negative number, maximum deceleration</param>
		/// <param name="dist">positive number, distance to travel</param>
		/// <returns>The maximum angular speed to stop at the destination</returns>
		private float MaxAngleSpeed(float accel, float dist, bool fast)
		{
			//myLogger.debugLog("accel: " + accel + ", dist: " + dist, "MaxAngleSpeed()");
			float actual = (float)Math.Sqrt(-2 * accel * dist);
			if (fast)
				return actual;
			return Math.Min(actual, dist * 2f);
		}

		/// <summary>
		/// Apply the calculated force ratios to the controller.
		/// </summary>
		public void MoveAndRotate()
		{
			CheckGrid();

			if (NavSet.Settings_Current.CollisionAvoidance)
			{
				if (!myPathfinder.CanMove)
				{
					//myLogger.debugLog("Pathfinder not allowing movement", "MoveAndRotate()");
					StopMove();
				}
				if (!myPathfinder.CanRotate)
				{
					//myLogger.debugLog("Pathfinder not allowing rotation", "MoveAndRotate()");
					StopRotate();
				}
			}

			//myLogger.debugLog("moveForceRatio: " + moveForceRatio + ", rotateForceRatio: " + rotateForceRatio + ", move length: " + moveForceRatio.Length(), "MoveAndRotate()");
			MyShipController controller = Block.Controller;

			myLogger.debugLog("stuck in: " + (m_stuckAt + StuckAfter - Globals.UpdateCount), "MoveAndRotate()");

			// only iff stuck for extra time (Nav may want to handle stuck)
			if (NavSet.Settings_Current.PathfinderCanChangeCourse && Globals.UpdateCount >= m_stuckAt + StuckAfter)
			{
				IMyEntity obstruction = myPathfinder.RotateObstruction ?? myPathfinder.MoveObstruction;
				// if cannot rotate and not calculating move, move away from obstruction
				if (obstruction != null)
				{
					obstruction = obstruction.GetTopMostParent();
					Vector3 displacement = Block.CubeBlock.GetPosition() - obstruction.GetCentre();
					Vector3 away;
					Vector3.Normalize(ref displacement, out away);
					myLogger.debugLog("Stuck, creating Waypoint to move away from obstruction", "MoveAndRotate()", Logger.severity.INFO);
					new Waypoint(this, NavSet, AllNavigationSettings.SettingsLevelName.NavWay, obstruction, displacement + away * (10f + NavSet.Settings_Current.DestinationRadius));
					m_stuckAt = Globals.UpdateCount + StuckAfter;
				}
			}

			// if all the force ratio values are 0, Autopilot has to stop the ship, MoveAndRotate will not
			if (moveForceRatio == Vector3.Zero && rotateForceRatio == Vector3.Zero)
			{
				//myLogger.debugLog("Stopping the ship", "MoveAndRotate()");
				MoveAndRotateStop();
				return;
			}

			// clamp values and invert operations MoveAndRotate will perform
			Vector3 moveControl; moveForceRatio.ApplyOperation(dim => MathHelper.Clamp(dim, -1, 1), out moveControl);

			Vector2 rotateControl = Vector2.Zero;
			rotateControl.X = MathHelper.Clamp(rotateForceRatio.X, -1, 1) * pixelsForMaxRotation;
			rotateControl.Y = MathHelper.Clamp(rotateForceRatio.Y, -1, 1) * pixelsForMaxRotation;

			float rollControl = MathHelper.Clamp(rotateForceRatio.Z, -1, 1) / RollControlMultiplier;

			myLogger.debugLog("moveControl: " + moveControl + ", rotateControl: " + rotateControl + ", rollControl: " + rollControl, "MoveAndRotate()");

			m_stopped = false;
			//myLogger.debugLog("Queueing Move and Rotate, move: " + moveControl + ", rotate: " + rotateControl + ", roll: " + rollControl + ", unstick: " + unstick, "MoveAndRotate()");
			MyAPIGateway.Utilities.TryInvokeOnGameThread(() => {
				myLogger.debugLog("Applying Move and Rotate, move: " + moveControl + ", rotate: " + rotateControl + ", roll: " + rollControl, "MoveAndRotate()");
				controller.MoveAndRotate(moveControl, rotateControl, rollControl);
			}, myLogger);
		}

		private void CheckGrid()
		{
			if (myGrid != Block.CubeGrid)
			{
				myLogger.debugLog(myGrid != null, "Grid Changed! from " + myGrid.getBestName() + " to " + Block.CubeGrid.getBestName(), "CheckGrid()", Logger.severity.INFO);
				myGrid = Block.CubeGrid;
				this.myThrust = new ThrustProfiler(Block.CubeBlock);
				this.myGyro = new GyroProfiler(myGrid);
				this.myPathfinder = new Pathfinder.Pathfinder(myGrid, NavSet, this);
			}
		}

		public bool ThrustersOverWorked(float ratio = 0.9f)
		{
			myLogger.debugLog(myThrust == null, "myThrust == null", "ThrustersOverWorked()", Logger.severity.FATAL);
			if (m_overworked)
				ratio -= 0.05f;
			m_overworked = myThrust.m_gravityReactRatio.AbsMax() >= ratio;
			return m_overworked;
		}

	}
}

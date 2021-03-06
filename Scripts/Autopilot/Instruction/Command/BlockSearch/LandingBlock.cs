﻿using System;
using System.Text;
using Rynchodon.Autopilot.Data;
using VRageMath;

namespace Rynchodon.Autopilot.Instruction.Command
{
	public class LandingBlock : ALocalBlock
	{

		public override ACommand Clone()
		{
			return new LandingBlock() { m_searchBlockName = m_searchBlockName.Clone(), m_forward = m_forward, m_upward = m_upward };
		}

		public override string Identifier
		{
			get { return "l"; }
		}

		public override string AddName
		{
			get { return "Landing Block"; }
		}

		public override string AddDescription
		{
			get { return "Block that will be landed on the target entity"; }
		}

		public override string Description
		{
			get
			{
				if (m_block.NullOrClosed())
					return "Missing block!";
				return "Land " + m_block.DisplayNameText + " on the target ship";
			}
		}

		protected override void ActionMethod(Movement.Mover mover)
		{
			PseudoBlock pseudo = new PseudoBlock(m_block, m_forward, m_upward);
			mover.NavSet.Settings_Task_NavRot.LandingBlock = pseudo;
			mover.NavSet.LastLandingBlock = pseudo;
		}
	}
}

﻿using System;
using Rynchodon.Autopilot.Movement;
using Rynchodon.Autopilot.Navigator;

namespace Rynchodon.Autopilot.Instruction.Command
{
	public class Stop : ASingleWord
	{

		public override ACommand Clone()
		{
			return new Stop();
		}

		public override string Identifier
		{
			get { return "stop"; }
		}

		public override string AddDescription
		{
			get { return "Stop the ship before continuing"; }
		}

		protected override void ActionMethod(Mover mover)
		{
			new Stopper(mover);
		}

	}
}

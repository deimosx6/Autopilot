﻿#define LOG_ENABLED //remove on build

using System;
using System.Collections.Generic;
//using System.Linq;
//using System.Text;

using Sandbox.Common;
using Sandbox.Common.Components;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using Ingame = Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using VRage;

namespace Rynchodon.AntennaRelay
{
	[MyEntityComponentDescriptor(typeof(MyObjectBuilder_LaserAntenna))]
	public class LaserAntenna : Receiver
	{
		internal static LinkedList<LaserAntenna> registry = new LinkedList<LaserAntenna>(); // to iterate over

		private Ingame.IMyLaserAntenna myLaserAntenna;

		protected override void DelayedInit()
		{
			base.DelayedInit();
			myLaserAntenna = Entity as Ingame.IMyLaserAntenna;
			registry.AddLast(this);

			log("init as antenna: " + CubeBlock.BlockDefinition.SubtypeName, "Init()", Logger.severity.TRACE);
			EnforcedUpdate = MyEntityUpdateEnum.EACH_100TH_FRAME;
		}

		public override void Close()
		{
			try
			{
				if (CubeBlock != null)
					registry.Remove(this);
			}
			catch (Exception e)
			{ alwaysLog("exception on removing from registry: " + e, "Close()", Logger.severity.WARNING); }
			CubeBlock = null;
			myLaserAntenna = null;
			MyObjectBuilder = null;
			myLastSeen = null;
			myMessages = null;
		}

		public override void UpdateAfterSimulation100()
		{
			if (!IsInitialized) return;
			if (Closed) return;
			try
			{
				if (!myLaserAntenna.IsWorking)
					return;

				Showoff.doShowoff(CubeBlock, myLastSeen.Values.GetEnumerator(), myLastSeen.Count);

				// stage 5 is the final stage. It is possible for one to be in stage 5, while the other is not
				MyObjectBuilder_LaserAntenna builder = CubeBlock.getSlim().GetObjectBuilder() as MyObjectBuilder_LaserAntenna;
				if (builder.targetEntityId != null)
					foreach (LaserAntenna lAnt in registry)
						if (lAnt.CubeBlock.EntityId == builder.targetEntityId)
							if (builder.State == 5 && (lAnt.CubeBlock.getSlim().GetObjectBuilder() as MyObjectBuilder_LaserAntenna).State == 5)
							{
								//log("Laser " + CubeBlock.gridBlockName() + " connected to " + lAnt.CubeBlock.gridBlockName(), "UpdateAfterSimulation100()", Logger.severity.DEBUG);
								foreach (LastSeen seen in myLastSeen.Values)
									lAnt.receive(seen);
								foreach (Message mes in myMessages)
									lAnt.receive(mes);
								break;
							}

				// send to attached receivers
				Receiver.sendToAttached(CubeBlock, myLastSeen);
				Receiver.sendToAttached(CubeBlock, myMessages);
			}
			catch (Exception e)
			{ alwaysLog("Exception: " + e, "UpdateAfterSimulation100()", Logger.severity.ERROR); }
		}
	}
}
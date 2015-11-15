using System;
using System.Collections.Generic;
using System.Text;
using Rynchodon.Autopilot.Data;
using Rynchodon.Autopilot.Movement;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage;
using VRage.ModAPI;
using VRageMath;
using Ingame = Sandbox.ModAPI.Ingame;

namespace Rynchodon.Autopilot.Navigator
{

	public class Grinder : NavigatorMover, INavigatorRotator
	{

		private const float MaxAngleRotate = 1f;
		private static readonly TimeSpan SearchTimeout = new TimeSpan(0, 1, 0);

		/// <summary>The grid claimed and the grid that claimed it.</summary>
		private static Dictionary<long, IMyCubeGrid> GridsClaimed = new Dictionary<long, IMyCubeGrid>();

		static Grinder()
		{
			MyAPIGateway.Entities.OnCloseAll += Entities_OnCloseAll;
		}

		private static void Entities_OnCloseAll()
		{
			MyAPIGateway.Entities.OnCloseAll -= Entities_OnCloseAll;
			GridsClaimed = null;
		}

		private enum Stage : byte { None, Intercept, Grind }

		private readonly Logger m_logger;
		private readonly MultiBlock<MyObjectBuilder_ShipGrinder> m_navGrind;
		private readonly Vector3 m_startPostion;
		private readonly float m_grinderOffset;
		private readonly float m_longestDimension;
		private readonly GridFinder m_finder;

		private GridCellCache m_enemyCells;
		private Vector3D m_targetPosition;
		private DateTime m_timeoutAt = DateTime.UtcNow + SearchTimeout;
		private ulong m_next_grinderFullCheck;
		private ulong m_next_grinderCheck;
		private bool m_grinderFull;
		private bool m_enableGrinders;

		private IMyCubeGrid value_enemy;
		private Stage value_stage;

		private Stage m_stage
		{
			get { return value_stage; }
			set
			{
				if (value == value_stage)
					return;
				m_logger.debugLog("Changing stage from " + value_stage + " to " + value, "set_m_stage()", Logger.severity.DEBUG);
				value_stage = value;

				if (value_stage == Stage.None)
					EnableGrinders(false);
			}
		}

		private IMyCubeGrid m_enemy
		{
			get { return value_enemy; }
		}

		private bool set_enemy(IMyCubeGrid value)
		{
			if (value == value_enemy)
				return true;

			if (value_enemy != null)
				if (GridsClaimed.Remove(value_enemy.EntityId))
					m_logger.debugLog("Removed " + value_enemy.getBestName() + " from GridsClaimed", "Move()", Logger.severity.TRACE);
				else
					m_logger.alwaysLog("Failed to remove " + value_enemy.getBestName() + " from GridsClaimed", "Move()", Logger.severity.WARNING);

			if (value != null)
			{
				if (GridsClaimed.ContainsKey(value.EntityId))
				{
					m_logger.debugLog("Already claimed: " + value.getBestName(), "set_m_enemy()", Logger.severity.INFO);
					return false;
				}
				else
					GridsClaimed.Add(value.EntityId, m_controlBlock.CubeGrid);
				
				m_enemyCells = GridCellCache.GetCellCache(value as IMyCubeGrid);
			}

			m_stage = Stage.None;
			value_enemy = value;
			return true;
		}

		public Grinder(Mover mover, AllNavigationSettings navSet, float maxRange)
			: base(mover, navSet)
		{
			this.m_logger = new Logger(GetType().Name, () => m_controlBlock.CubeGrid.DisplayName, () => m_stage.ToString());
			this.m_startPostion = m_controlBlock.CubeBlock.GetPosition();
			this.m_longestDimension = m_controlBlock.CubeGrid.GetLongestDim();

			PseudoBlock navBlock = m_navSet.Settings_Current.NavigationBlock;
			m_navGrind = navBlock.Block is Ingame.IMyShipGrinder
				? new MultiBlock<MyObjectBuilder_ShipGrinder>(navBlock.Block)
				: new MultiBlock<MyObjectBuilder_ShipGrinder>(m_mover.Block.CubeGrid);

			if (m_navGrind.FunctionalBlocks == 0)
			{
				m_logger.debugLog("no working grinders", "Grinder()", Logger.severity.INFO);
				return;
			}

			m_grinderOffset = m_navGrind.Block.GetLengthInDirection(m_navGrind.Block.GetFaceDirection()[0]) * 0.5f + 2.5f;
			if (m_navSet.Settings_Current.DestinationRadius > m_longestDimension)
			{
				m_logger.debugLog("Reducing DestinationRadius from " + m_navSet.Settings_Current.DestinationRadius + " to " + m_longestDimension, "MinerVoxel()", Logger.severity.DEBUG);
				m_navSet.Settings_Task_NavRot.DestinationRadius = m_longestDimension;
			}

			this.m_finder = new GridFinder(m_navSet, mover.Block, maxRange);
			this.m_finder.GridCondition = GridCondition;

			m_navSet.Settings_Task_NavRot.NavigatorMover = this;
			m_navSet.Settings_Task_NavRot.NavigatorRotator = this;
		}

		~Grinder()
		{
			try { set_enemy(null); }
			catch { }
		}

		public override void Move()
		{
			if (m_navGrind.FunctionalBlocks == 0)
			{
				m_logger.debugLog("No functional grinders remaining", "Move()", Logger.severity.INFO);
				m_navSet.OnTaskComplete_NavRot();
				EnableGrinders(false);
				return;
			}
			
			if (Globals.UpdateCount >= m_next_grinderCheck)
				EnableGrinders(m_enableGrinders);

			if (GrinderFull())
			{
				m_logger.debugLog("Grinders are full", "Move()", Logger.severity.INFO);
				m_navSet.OnTaskComplete_NavRot();
				EnableGrinders(false);
				return;
			}

			m_finder.Update();
			if (!set_enemy(m_finder.Grid != null ? m_finder.Grid.Entity as IMyCubeGrid : null) || m_enemy == null)
			{
				m_mover.StopMove();
				if (DateTime.UtcNow >= m_timeoutAt)
				{
					m_logger.debugLog("Search timed out", "Move()");
					m_navSet.OnTaskComplete_NavRot();
					EnableGrinders(false);
				}
				return;
			}

			m_timeoutAt = DateTime.UtcNow + SearchTimeout;
			Vector3 targetCentre = m_enemy.GetCentre();

			Vector3 enemyVelocity = m_enemy.GetLinearVelocity();
			if (enemyVelocity.LengthSquared() > 10f)
			{
				float targetLongest = m_enemy.LocalAABB.GetLongestDim();

				Vector3 furthest = targetCentre + enemyVelocity * 1000000f;
				Line approachTo = new Line(targetCentre, furthest, false);

				float multi = m_stage == Stage.Intercept ? 0.5f : 1f;
				if (!approachTo.PointInCylinder(targetLongest * multi, m_navGrind.WorldPosition))
				{
					m_targetPosition = targetCentre;
					Vector3 direction = furthest - targetCentre;
					direction.Normalize();
					Move_Intercept(targetCentre + direction * (targetLongest + m_longestDimension));
					return;
				}
			}

			Move_Grind();
		}

		private void Move_Grind()
		{
			if (m_stage != Stage.Grind)
			{
				m_logger.debugLog("Now grinding", "Move_Grind()", Logger.severity.DEBUG);
				m_navSet.OnTaskComplete_NavMove();
				EnableGrinders(true);
				m_stage = Stage.Grind;
				m_navSet.Settings_Task_NavMove.DestinationEntity = m_enemy;
			}

			Vector3D grindPosition = m_navGrind.WorldPosition;
			Vector3I cellPosition = m_enemyCells.GetClosestOccupiedCell(m_controlBlock.CubeGrid.GetCentre());
			IMySlimBlock block = m_enemy.GetCubeBlock(cellPosition);
			if (block == null)
			{
				m_logger.debugLog("No block found at cell position: " + cellPosition, "Move_Grind()", Logger.severity.INFO);
				return;
			}
			m_logger.debugLog("block: " + block, "Move_Grind()");
			m_targetPosition = m_enemy.GridIntegerToWorld(m_enemy.GetCubeBlock(cellPosition).Position);
			m_logger.debugLog("cellPosition: " + cellPosition + ", block: " + m_enemy.GetCubeBlock(cellPosition) + ", world: " + m_targetPosition, "Move_Grind()");

			if (m_navSet.Settings_Current.DistanceAngle > MaxAngleRotate)
			{
				if (!m_mover.myPathfinder.CanRotate)
				{
					m_logger.debugLog("Extricating ship from target", "Move_Grind()");
					m_navSet.Settings_Task_NavMove.SpeedMaxRelative = float.MaxValue;
					m_mover.CalcMove(m_navGrind, m_targetPosition + m_navGrind.WorldMatrix.Backward * 100f, m_enemy.GetLinearVelocity(), false);
				}
				else
				{
					m_logger.debugLog("Waiting for angle to match", "Move_Grind()");
					m_mover.CalcMove(m_navGrind, m_navGrind.WorldPosition, m_enemy.GetLinearVelocity(), false);
				}
				return;
			}

			float distSq = Vector3.DistanceSquared(m_targetPosition, grindPosition);
			float offset = m_grinderOffset + m_enemy.GridSize;
			float offsetEpsilon = offset + 1f;
			if (distSq > offsetEpsilon * offsetEpsilon)
			{
				Vector3D targetToGrinder = grindPosition - m_targetPosition;
				targetToGrinder.Normalize();

				m_logger.debugLog("far away(" + distSq + "), moving to " + (m_targetPosition + targetToGrinder * offset), "Move_Grind()");
				m_navSet.Settings_Task_NavMove.SpeedMaxRelative = float.MaxValue;
				m_mover.CalcMove(m_navGrind, m_targetPosition + targetToGrinder * offset, m_enemy.GetLinearVelocity(), true);
			}
			else
			{
				m_logger.debugLog("close(" + distSq + "), moving to " + m_targetPosition, "Move_Grind()");
				m_navSet.Settings_Task_NavMove.SpeedMaxRelative = 1f;
				m_mover.CalcMove(m_navGrind, m_targetPosition, m_enemy.GetLinearVelocity(), true);
			}
		}

		private void Move_Intercept(Vector3 position)
		{
			if (m_stage != Stage.Intercept)
			{
				m_logger.debugLog("Now intercepting", "Move_Intercept()", Logger.severity.DEBUG);
				m_navSet.OnTaskComplete_NavMove();
				EnableGrinders(false);
				m_navSet.Settings_Task_NavMove.SpeedMaxRelative = float.MaxValue;
				m_stage = Stage.Intercept;
			} 

			m_logger.debugLog("Moving to " + position, "Move_Intercept()");
			m_mover.CalcMove(m_navGrind, position, m_enemy.GetLinearVelocity());
		}

		public void Rotate()
		{
			if (m_enemy == null || (m_navSet.DistanceLessThan(1f) && m_navSet.Settings_Current.DistanceAngle <= MaxAngleRotate))
			{
				m_mover.StopRotate();
				return;
			}

			m_logger.debugLog("rotating to " + m_targetPosition, "Rotate()");
			m_mover.CalcRotate(m_navGrind, RelativeDirection3F.FromWorld(m_navGrind.Grid, m_targetPosition - m_navGrind.WorldPosition));
		}

		public override void AppendCustomInfo(StringBuilder customInfo)
		{
			customInfo.AppendLine("Grinder:");
			if (m_enemy == null)
			{
				customInfo.Append("Searching for a ship, timeout in ");
				customInfo.Append((m_timeoutAt - DateTime.UtcNow).Seconds);
				customInfo.AppendLine(" seconds.");
				switch (m_finder.m_reason)
				{
					case GridFinder.ReasonCannotTarget.Too_Far:
						customInfo.AppendLine("Best target is too far away");
						break;
					case GridFinder.ReasonCannotTarget.Too_Fast:
						customInfo.AppendLine("Best target is moving too quickly");
						break;
					case GridFinder.ReasonCannotTarget.Grid_Condition:
						IMyCubeGrid claimedBy;
						if (GridsClaimed.TryGetValue(m_finder.m_reasonGrid, out claimedBy))
						{
							if (m_controlBlock.CubeBlock.canConsiderFriendly(claimedBy))
							{
								customInfo.Append("Best target is claimed by ");
								customInfo.AppendLine(claimedBy.DisplayName);
							}
							else
								customInfo.AppendLine("Best target is claimed by another recyler.");
						}
						else
							customInfo.AppendLine("Best target was claimed, should be available shortly.");
						break;
				}
				return;
			}

			switch (m_stage)
			{
				case Stage.Intercept:
					customInfo.AppendLine("Moving towards a ship");
					break;
				case Stage.Grind:
					customInfo.AppendLine("Reducing a ship to its constituent parts");
					break;
			}
		}

		private void EnableGrinders(bool enable)
		{
			//if (enable)
			//	m_logger.debugLog("enabling grinders", "EnableGrinders()", Logger.severity.DEBUG);
			//else
			//	m_logger.debugLog("disabling grinders", "EnableGrinders()", Logger.severity.DEBUG);

			var allGrinders = CubeGridCache.GetFor(m_controlBlock.CubeGrid).GetBlocksOfType(typeof(MyObjectBuilder_ShipGrinder));
			MyAPIGateway.Utilities.TryInvokeOnGameThread(() => {
				foreach (Ingame.IMyShipGrinder grinder in allGrinders)
					if (!grinder.Closed)
						grinder.RequestEnable(enable);
			}, m_logger);

			m_next_grinderCheck = Globals.UpdateCount + 1000ul;
			m_enableGrinders = enable;
		}

		private bool GrinderFull()
		{
			if (Globals.UpdateCount < m_next_grinderFullCheck)
				return m_grinderFull;
			m_next_grinderFullCheck = Globals.UpdateCount + 100ul;

			MyFixedPoint content = 0, capacity = 0;
			int grinderCount = 0;
			var allGrinders = CubeGridCache.GetFor(m_controlBlock.CubeGrid).GetBlocksOfType(typeof(MyObjectBuilder_ShipGrinder));
			if (allGrinders == null)
				return true;

			foreach (Ingame.IMyShipGrinder grinder in allGrinders)
			{
				IMyInventory grinderInventory = (IMyInventory)Ingame.TerminalBlockExtentions.GetInventory(grinder, 0);
				content += grinderInventory.CurrentVolume;
				capacity += grinderInventory.MaxVolume;
				grinderCount++;
			}

			m_grinderFull = (float)content / (float)capacity >= 0.9f;
			return m_grinderFull;
		}

		private bool GridCondition(IMyCubeGrid grid)
		{
			if (m_enemy == grid)
				return true;

			IMyCubeGrid claimedBy;
			if (!GridsClaimed.TryGetValue(grid.EntityId, out claimedBy))
				return true;

			if (claimedBy != m_controlBlock.CubeGrid)
				return false;

			m_logger.debugLog("This grid has staked a claim but forgot about it", "GridCondition()", Logger.severity.INFO);
			set_enemy(null);
			if (GridsClaimed.Remove(grid.EntityId))
				m_logger.debugLog("removed claim", "GridCondition()");
			else
				m_logger.alwaysLog("Failed to remove claim: " + grid.DisplayName, "GridCondition()", Logger.severity.WARNING);
			return true;
		}

	}
}

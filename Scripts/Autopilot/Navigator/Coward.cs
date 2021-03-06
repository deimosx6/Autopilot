using System.Text;
using Rynchodon.AntennaRelay;
using Rynchodon.Autopilot.Data;
using Rynchodon.Autopilot.Movement;
using Sandbox.ModAPI;
using VRageMath;

namespace Rynchodon.Autopilot.Navigator
{
	/// <summary>
	/// Screams and runs away.
	/// </summary>
	public class Coward : NavigatorMover, IEnemyResponse
	{

		private readonly Logger m_logger;

		private LastSeen m_enemy;
		private Vector3 m_flyDirection;

		public Coward(Mover mover, AllNavigationSettings navSet)
			: base(mover, navSet)
		{
			this.m_logger = new Logger(GetType().Name, () => m_controlBlock.CubeGrid.DisplayName);

			m_logger.debugLog("Initialized", "Coward()");
		}

		#region IEnemyResponse Members

		public bool CanRespond()
		{
			return m_mover.CanMoveForward(m_mover.Block.Pseudo);
		}

		public bool CanTarget(IMyCubeGrid grid)
		{
			return true;
		}

		public void UpdateTarget(LastSeen enemy)
		{
			m_enemy = enemy;
		}

		#endregion

		public override void Move()
		{
			m_logger.debugLog("entered", "Move()");

			if (m_enemy == null)
			{
				m_mover.StopMove();
				return;
			}

			Vector3 position = m_mover.Block.CubeBlock.GetPosition();
			m_flyDirection = position - m_enemy.GetPosition();
			m_flyDirection.Normalize();

			Vector3 destination = position + m_flyDirection * 1000000f;
			m_mover.CalcMove(m_mover.Block.Pseudo, destination, Vector3.Zero);
		}

		public void Rotate()
		{
			m_logger.debugLog("entered", "Rotate()");
			
			if (m_enemy == null)
			{
				m_mover.StopRotate();
				return;
			}

			m_mover.CalcRotate(m_mover.Block.Pseudo, RelativeDirection3F.FromWorld(m_mover.Block.CubeGrid, m_flyDirection));
		}

		public override void AppendCustomInfo(StringBuilder customInfo)
		{
			if (m_enemy != null)
			{
				customInfo.Append("Running like a coward from an enemy at ");
				customInfo.AppendLine(m_enemy.GetPosition().ToPretty());
			}
		}

	}
}

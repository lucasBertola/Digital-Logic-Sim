using Seb.Types;
using UnityEngine;

namespace DLS.Game
{
	public interface IMoveable : IInteractable
	{
		Vector2 Position { get; set; }
		Vector2 MoveStartPosition { get; set; }
		Vector2 StraightLineReferencePoint { get; set; }
		bool HasReferencePointForStraightLineMovement { get; set; }
		bool IsSelected { get; set; }
		bool IsValidMovePos { get; set; }
		Vector2 SnapPoint { get; }
		Bounds2D SelectionBoundingBox { get; }
		Bounds2D BoundingBox { get; }
		int ID { get; }

		// Optional layout constraints honoured by Clean Up (0 = automatic). Bigger LayoutCol = further
		// right, bigger LayoutRow = higher; equal values mean same column / same line.
		int LayoutCol { get; set; }
		int LayoutRow { get; set; }

		public bool ShouldBeIncludedInSelectionBox(Vector2 selectionCentre, Vector2 selectionSize);
	}
}
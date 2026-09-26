namespace DLS.Game
{
	// Note: updated when drawing world
	public static class InteractionState
	{
		public static bool MouseIsOverUI;

		static readonly IInteractable unspecifiedElement = new UnspecifiedInteractableElement();

		// The interactable element currently under the mouse.
		// Note: set to null prior to drawing each frame, and set during the drawing process
		public static IInteractable ElementUnderMouse { get; private set; }
		public static IInteractable ElementUnderMousePrevFrame { get; private set; }
		
		public static PinInstance PinUnderMouse => ElementUnderMouse as PinInstance;

		// Element the right-click menu should act on when the thing under the mouse is a sub-part of it that
		// registers as "unspecified" (an input pin's state toggle, for example): the toggle keeps its own
		// left-click behaviour, but the context menu is the pin's.
		public static IInteractable ContextElementUnderMouse { get; private set; }
		public static IInteractable ElementForContextMenu => ElementUnderMouse == null || ElementUnderMouse == unspecifiedElement ? ContextElementUnderMouse ?? ElementUnderMouse : ElementUnderMouse;
		public static void NotifyContextElementUnderMouse(IInteractable element) => ContextElementUnderMouse = element;

		public static void NotifyElementUnderMouse(IInteractable element)
		{
			ElementUnderMouse = element;
		}

		// Notify that something is under the mouse, without specifying what it is.
		// Useful for interactions with sub-elements (such as the status display on an input pin) which don't have their own associated object and are handled specially
		public static void NotifyUnspecifiedElementUnderMouse() => NotifyElementUnderMouse(unspecifiedElement);
		

		public static void ClearFrame()
		{
			ElementUnderMousePrevFrame = ElementUnderMouse;
			ElementUnderMouse = null;
			ContextElementUnderMouse = null;
		}

		public static void Reset()
		{
			ElementUnderMouse = null;
			ContextElementUnderMouse = null;
			MouseIsOverUI = false;
		}

		class UnspecifiedInteractableElement : IInteractable
		{
		}
	}
}
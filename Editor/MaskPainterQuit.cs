using UnityEditor;

namespace DocMe.MaskPainter.Editor
{
    /// <summary>
    /// Asks to save a painting with changes when Unity quits. Closing or maximizing a window never asks: the painting
    /// outlives its window and comes back when the window opens again, so only quitting can lose it.
    /// </summary>
    [InitializeOnLoad]
    static class MaskPainterQuit
    {
        static MaskPainterQuit()
        {
            EditorApplication.wantsToQuit -= AskToSave;
            EditorApplication.wantsToQuit += AskToSave;
        }

        static bool AskToSave()
        {
            foreach (var session in MaskSession.AllUnsaved())
            {
                var name = session.Renderer != null ? session.Renderer.name : MaskText.T("quit.mesh");
                int choice = EditorUtility.DisplayDialogComplex(MaskText.T("window.title"),
                    MaskText.T("quit.message", name),
                    MaskText.T("quit.save"), MaskText.T("dialog.cancel"), MaskText.T("quit.discard"));
                if (choice == 1) return false;
                if (choice == 2) continue;
                if (!MaskPainterWindow.SaveForQuit(session)) return false;
            }
            return true;
        }
    }
}

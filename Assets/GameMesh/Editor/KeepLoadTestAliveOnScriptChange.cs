using UnityEditor;

namespace GameMesh.Editor
{
    // A domain reload aborts every load-test socket in the same second.
    // Gateways then see a peer close on each port while their send buffers still have data.
    [InitializeOnLoad]
    static class KeepLoadTestAliveOnScriptChange
    {
        const string Key = "ScriptCompilationDuringPlay";

        // Unity Preferences: 0 continue playing (reloads the domain), 1 after play mode ends, 2 stop and recompile.
        const int RecompileAfterFinishedPlaying = 1;

        static KeepLoadTestAliveOnScriptChange()
        {
            if (EditorPrefs.GetInt(Key, 0) == RecompileAfterFinishedPlaying)
                return;
            EditorPrefs.SetInt(Key, RecompileAfterFinishedPlaying);
        }
    }
}

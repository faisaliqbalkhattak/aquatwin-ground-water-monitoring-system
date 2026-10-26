using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AquaTwin.Editor
{
    /// <summary>One scene identity, with a disposable preview of the actual runtime UI.</summary>
    [InitializeOnLoad]
    public static class NetworkSceneSetup
    {
        private static double next;
        private static bool shuttingDown, previewFailed;
        static NetworkSceneSetup()
        {
            EditorApplication.delayCall += Ensure;
            EditorSceneManager.sceneOpened += (_,__) => EditorApplication.delayCall += Ensure;
            EditorApplication.update += Preview;
            AssemblyReloadEvents.beforeAssemblyReload += () => {shuttingDown=true;Clear();};
            EditorApplication.quitting += () => {shuttingDown=true;Clear();};
            EditorSceneManager.sceneClosing += (_,__) => Clear();
            EditorApplication.playModeStateChanged += state =>
            {
                if(state==PlayModeStateChange.ExitingEditMode)Clear();
                if(state==PlayModeStateChange.EnteredEditMode)EditorApplication.delayCall+=Ensure;
            };
        }
        [MenuItem("AquaTwin/Set Up Network Dashboard Scene",priority=1)]
        public static void Ensure()
        {
            if(shuttingDown||EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling||EditorApplication.isUpdating)return;
            var scene=SceneManager.GetActiveScene();
            if(!scene.IsValid()||!scene.isLoaded||scene.name!="SampleScene")return;
            bool changed=false;
            foreach(var root in scene.GetRootGameObjects())
                if(root.name=="AquaTwin_Environment"||root.name=="DashboardCanvas")
                {Undo.DestroyObjectImmediate(root);changed=true;}
            var dashboard=scene.GetRootGameObjects().SelectMany(x=>x.GetComponentsInChildren<FleetDashboard>(true)).FirstOrDefault();
            if(dashboard==null)
            {
                var host=new GameObject("AquaTwin Well Network");Undo.RegisterCreatedObjectUndo(host,"Create network dashboard");
                dashboard=Undo.AddComponent<FleetDashboard>(host);changed=true;
            }
            if(!EditorBuildSettings.scenes.Any(x=>x.path==scene.path))
                EditorBuildSettings.scenes=EditorBuildSettings.scenes.Concat(new[]{new EditorBuildSettingsScene(scene.path,true)}).ToArray();
            if(changed){EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);}
            next=EditorApplication.timeSinceStartup+1;
            previewFailed=false;
        }
        private static void Preview()
        {
            if(shuttingDown||previewFailed||EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling||EditorApplication.isUpdating||BuildPipeline.isBuildingPlayer||EditorApplication.timeSinceStartup<next)return;
            next=EditorApplication.timeSinceStartup+1;
            if(SceneManager.GetActiveScene().name!="SampleScene")return;
            var dashboard=Object.FindFirstObjectByType<FleetDashboard>();
            if(dashboard!=null)
            {
                try { dashboard.InitializeEditorPreview(); }
                catch(System.Exception error) { previewFailed=true;Clear();Debug.LogException(error); }
            }
        }
        private static void Clear()
        {
            foreach(var dashboard in Object.FindObjectsByType<FleetDashboard>(FindObjectsSortMode.None))dashboard.ClearEditorPreview();
        }
    }
}

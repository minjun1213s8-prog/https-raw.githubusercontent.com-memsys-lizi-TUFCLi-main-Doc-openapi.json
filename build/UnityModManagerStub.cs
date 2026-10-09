using System;

namespace UnityModManagerNet
{
    public static class UnityModManager
    {
        public class ModEntry
        {
            public ModInfo Info;
            public ModLogger Logger;
            public bool Enabled;
            public Func<ModEntry, bool, bool> OnToggle;
            public Action<ModEntry> OnGUI;
            public Action<ModEntry> OnSaveGUI;
            public Action<ModEntry, float> OnUpdate;
            public Func<ModEntry, bool> OnUnload;

            public class ModInfo
            {
                public string Id;
            }

            public class ModLogger
            {
                public void Log(string str) { }
                public void Error(string str) { }
            }
        }
    }
}

#if UNITY_EDITOR
using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

public class StoragePermissionPostProcessor : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 999;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(manifestPath))
        {
            Debug.LogError("[PostProcessor] AndroidManifest.xml not found at: " + manifestPath);
            return;
        }

        XmlDocument doc = new XmlDocument();
        doc.Load(manifestPath);

        string androidNs = "http://schemas.android.com/apk/res/android";
        XmlNamespaceManager nsmgr = new XmlNamespaceManager(doc.NameTable);
        nsmgr.AddNamespace("android", androidNs);

        XmlNode manifestNode = doc.SelectSingleNode("manifest");
        if (manifestNode == null) return;

        string[] permissionsToEnsure = new string[]
        {
            "android.permission.MANAGE_EXTERNAL_STORAGE",
            "android.permission.READ_EXTERNAL_STORAGE",
            "android.permission.WRITE_EXTERNAL_STORAGE",
            "android.permission.INTERNET",
            "android.permission.POST_NOTIFICATIONS"
        };

        // Standard for loop for Unity build compatibility
        for (int i = 0; i < permissionsToEnsure.Length; i++)
        {
            string perm = permissionsToEnsure[i];
            XmlNode existingNode = manifestNode.SelectSingleNode($"uses-permission[@android:name='{perm}']", nsmgr);

            if (existingNode == null)
            {
                XmlElement newPerm = doc.CreateElement("uses-permission");
                newPerm.SetAttribute("name", androidNs, perm);
                manifestNode.AppendChild(newPerm);
                Debug.Log($"[PostProcessor] Successfully injected: {perm}");
            }
        }

        doc.Save(manifestPath);
    }
}
#endif
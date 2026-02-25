using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

[DefaultExecutionOrder(-1000)]
public class CSVReader : MonoBehaviour
{
    [Tooltip("CSV File name at Assets(Editor) or next to exe file(Build)")]
    [SerializeField]
    private string csvFilename = "Settings.txt";

    private void Awake()
    {
        if (DataPair.Count > 0) return;
        string rawData = BuildLoadData(csvFilename);
        var lines = rawData.Replace('\r', '\n').Split('\n');
        foreach (var line in lines)
        {
            var rows = line.Split(',');
            if (rows.Length < 2) continue;
            string key = rows[0].Trim();
            if (DataPair.ContainsKey(key)) { DataPair.Remove(key); }
            DataPair.Add(key, rows[1].Trim());
        }
    }

    private string BuildLoadData(string csvFilename)
    {
        string path = Application.dataPath; //AppDomain.CurrentDomain.BaseDirectory;

        if (!Application.isEditor)
            path = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        path = Path.Combine(path, csvFilename);
        if (!File.Exists(path))
        {
            Debug.LogWarning($"[CSVReader] CSV 파일을 찾을 수 없습니다: [{path}]");
            return string.Empty;
        }
        try
        {
            var raw = File.ReadAllBytes(path);
            return Encoding.UTF8.GetString(raw);
        }
        catch (System.Exception)
        {
            return string.Empty;
        }
    }

    public static readonly Dictionary<string, string> DataPair
        = new Dictionary<string, string>();

    public static int GetIntValue(string key, int defaultValue = 0)
    {
        if (DataPair.TryGetValue(key, out var data) && int.TryParse(data, out var res))
            return res;
        return defaultValue;
    }

    public static float GetFloatValue(string key, float defaultValue = 0f)
    {
        if (DataPair.TryGetValue(key, out var data) && float.TryParse(data, out var res))
            return res;
        return defaultValue;
    }

    public static string GetStringValue(string key, string defaultValue = "")
    {
        if (DataPair.TryGetValue(key, out var data))
            return data;
        return defaultValue;
    }

    public static Color GetColorValue(string key, Color defaultColor = default)
    {
        if (DataPair.TryGetValue(key, out var data) && ColorUtility.TryParseHtmlString(data, out var res))
            return res;
        return defaultColor;
    }
}
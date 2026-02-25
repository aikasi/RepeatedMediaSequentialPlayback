using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public class Logger : MonoBehaviour
{
    public static Logger Instance { get; private set; }

    [Tooltip("Initialize automatically on Awake, saving logs in PersistantDataPath/Logs")]
    [SerializeField]
    private bool autoInitialize = true;

    [Tooltip("Whether to log Debug.Log/Exception automatically")]
    [SerializeField]
    private bool logConsole = false;

    [Tooltip("Maximum Log File count before deleting the oldest. Set it to 0 to not delete")]
    [SerializeField, Min(0)]
    private int maxFileCount = 1000;

    [Tooltip("Whether to append the current time automatically")]
    [SerializeField]
    private bool appendTime = true;

    private StreamWriter writer;
    private readonly Queue<string> logQueue = new();
    private Task writeTask;
    private bool finishing = false;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        // logConsole 옵션과 무관하게 Error, Warning, Exception은 반드시 로컬 파일에 기록하도록 개선
        Application.logMessageReceivedThreaded += (msg, _, type) => 
        {
            if (logConsole || type == LogType.Error || type == LogType.Exception || type == LogType.Warning || type == LogType.Assert)
            {
                // [필터링] AVPro 동적 생성 시 발생하는 시스템 초기화 과정의 무해한 에러 로그 차단
                if (msg.Contains("[AVProVideo] No MediaReference specified") || 
                    msg.Contains("[AVProVideo] No file path specified"))
                {
                    return; 
                }

                Enqueue($"[{type}] {msg}");
            }
        };
        if (autoInitialize)
            SetPath(Path.Combine(Application.persistentDataPath, "Logs", DateTime.Now.ToString("yyMMdd-HHmmss") + ".log"));
    }

    /// <summary>
    /// Set path to log file, automatically initializing it
    /// </summary>
    public void SetPath(string newPath)
    {
        var dir = Path.GetDirectoryName(newPath);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        if (maxFileCount > 0)
        {
            if (!string.IsNullOrEmpty(dir))
            {
                var logFiles = Directory
                    .GetFiles(dir, "*.log")
                    .Select(path => new FileInfo(path))
                    // sort ascending by creation time (oldest first)
                    .OrderBy(fi => fi.CreationTimeUtc)
                    .ToList();

                int excess = logFiles.Count - maxFileCount + 1;
                // +1 because we're about to create/open a new one
                for (int i = 0; i < excess; i++)
                {
                    try
                    {
                        logFiles[i].Delete();
                    }
                    catch (IOException e)
                    {
                        Debug.LogWarning($"Logger: failed to delete old log {logFiles[i].Name}: {e.Message}");
                    }
                }
            }
        }

        finishing = true;
        FinishWriting();

        writer = new StreamWriter(newPath, append: true, encoding: Encoding.UTF8)
        { AutoFlush = false };

        finishing = false;
        writeTask = null;
    }

    /// <summary>
    /// Enqueue new log manually
    /// </summary>
    public void Enqueue(string log)
    {
        if (appendTime) log = $"{DateTime.Now:HH:mm:ss.fff}) {log}";
        logQueue.Enqueue(log);
    }

    private void Update()
    {
        if (writer == null || finishing) return;

        if (writeTask != null && !writeTask.IsCompleted) return;

        if (logQueue.Count == 0) return;

        // drain queue
        var sb = new StringBuilder();
        while (logQueue.Count > 0)
            sb.AppendLine(logQueue.Dequeue());
        writeTask = WriteAndFlushAsync(sb.ToString());
        async Task WriteAndFlushAsync(string text)
        {
            await writer.WriteAsync(text);
            await writer.FlushAsync();
        }
    }

    private void FinishWriting()
    {
        if (writer == null) return;

        writeTask?.Wait();
        writeTask = null;

        if (logQueue.Count > 0)
        {
            var sb = new StringBuilder();
            while (logQueue.Count > 0)
                sb.AppendLine(logQueue.Dequeue());
            writer.Write(sb.ToString());
        }
        writer.Flush();
        writer.Dispose();
        writer = null;
    }

    private void OnApplicationQuit()
    {
        finishing = true;
        FinishWriting();
    }
}
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ImageOpt;

public class BatchedTextureCopier : MonoBehaviour
{
    public List<(Texture2D source, Texture2D destination)> copyTasks = new();

    public int copiesPerFrame = 20;

    private Coroutine _copyCoroutine;

    public bool IsRunning => _copyCoroutine != null;

    public static BatchedTextureCopier instance
    {
        get
        {
            var go = new GameObject("BatchedTextureCopier");
            var _instance = go.AddComponent<BatchedTextureCopier>();
            DontDestroyOnLoad(go);
            return _instance;
        }
    }

    public void StartCopying()
    {
        if (_copyCoroutine != null)
        {
            StopCoroutine(_copyCoroutine);
        }

        _copyCoroutine = StartCoroutine(ProcessCopyQueue());
    }

    private IEnumerator ProcessCopyQueue()
    {
        var queue = new Queue<(Texture2D source, Texture2D destination)>(copyTasks);
        copyTasks.Clear();

        while (queue.Count > 0)
        {
            for (var i = 0; i < copiesPerFrame; i++)
            {
                if (queue.Count == 0)
                    break;

                var task = queue.Dequeue();
                if (task.source != null && task.destination != null)
                {
                    Graphics.CopyTexture(task.source, task.destination);
                }
            }

            yield return null;
        }

        _copyCoroutine = null;
    }
}
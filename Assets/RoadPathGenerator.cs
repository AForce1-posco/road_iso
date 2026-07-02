using System.Collections.Generic;
using UnityEngine;

public class RoadPathGenerator : MonoBehaviour
{
    [Header("Road")]
    public float pointSpacing = 1.0f;
    public float gizmoSize = 0.2f;

    [Header("ISO")]
    public Transform isoPathParent;

    [HideInInspector] public List<Vector3> roadPoints = new List<Vector3>();
    [HideInInspector] public List<Vector3> isoPoints = new List<Vector3>();

    private List<Transform> roadNodes = new List<Transform>();
    private List<Transform> isoNodes = new List<Transform>();

    void Start()
    {
        ReadRoadNodes();
        GeneratePathFromNodes(roadNodes, roadPoints);

        if (isoPathParent != null)
        {
            ReadISONodes();
            GeneratePathFromNodes(isoNodes, isoPoints);
        }

        Debug.Log($"Road Points: {roadPoints.Count}");
        Debug.Log($"ISO Points: {isoPoints.Count}");
    }

    void ReadRoadNodes()
    {
        roadNodes.Clear();

        GameObject road = GameObject.Find("Road1");
        if (road == null)
        {
            Debug.LogError("Road1 not found");
            return;
        }

        Transform spline = road.transform.Find("Spline");
        if (spline == null)
        {
            Debug.LogError("Spline not found");
            return;
        }

        foreach (Transform child in spline)
        {
            if (child.name.StartsWith("Node"))
                roadNodes.Add(child);
        }

        roadNodes.Sort((a, b) =>
        {
            int ia = int.Parse(a.name.Replace("Node", ""));
            int ib = int.Parse(b.name.Replace("Node", ""));
            return ia.CompareTo(ib);
        });
    }

    void ReadISONodes()
    {
        isoNodes.Clear();

        foreach (Transform child in isoPathParent)
        {
            if (child.name.StartsWith("ISO_"))
                isoNodes.Add(child);
        }

        isoNodes.Sort((a, b) =>
        {
            int ia = int.Parse(a.name.Replace("ISO_", ""));
            int ib = int.Parse(b.name.Replace("ISO_", ""));
            return ia.CompareTo(ib);
        });
    }

    void GeneratePathFromNodes(List<Transform> nodes, List<Vector3> output)
    {
        output.Clear();

        if (nodes.Count < 2)
            return;

        for (int i = 0; i < nodes.Count - 1; i++)
        {
            Vector3 p0 = nodes[Mathf.Max(i - 1, 0)].position;
            Vector3 p1 = nodes[i].position;
            Vector3 p2 = nodes[i + 1].position;
            Vector3 p3 = nodes[Mathf.Min(i + 2, nodes.Count - 1)].position;

            float dist = Vector3.Distance(p1, p2);
            int count = Mathf.Max(2, Mathf.CeilToInt(dist / pointSpacing));

            for (int j = 0; j < count; j++)
            {
                float t = j / (float)count;
                output.Add(CatmullRom(p0, p1, p2, p3, t));
            }
        }

        output.Add(nodes[nodes.Count - 1].position);
    }

    Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        return 0.5f *
        (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t * t +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t * t * t
        );
    }

    void OnDrawGizmos()
    {
        if (roadPoints != null && roadPoints.Count > 1)
        {
            Gizmos.color = Color.green;
            for (int i = 0; i < roadPoints.Count - 1; i++)
                Gizmos.DrawLine(roadPoints[i], roadPoints[i + 1]);
        }

        if (isoPoints != null && isoPoints.Count > 1)
        {
            Gizmos.color = Color.cyan;
            for (int i = 0; i < isoPoints.Count - 1; i++)
                Gizmos.DrawLine(isoPoints[i], isoPoints[i + 1]);

            foreach (Vector3 p in isoPoints)
                Gizmos.DrawSphere(p, gizmoSize);
        }
    }
}
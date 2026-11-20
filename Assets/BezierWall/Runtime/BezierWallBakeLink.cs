using System;
using UnityEngine;

namespace MagicTavern.BezierWall
{
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class BezierWallBakeLink : MonoBehaviour
    {
        [HideInInspector] public string DraftGuid;
        [HideInInspector] public Mesh GeneratedMesh;
        [HideInInspector] public Material GeneratedMaterial;
        [HideInInspector] public Material LastAppliedMaterial;
        [HideInInspector] public PolygonCollider2D GeneratedCollider;
        [HideInInspector] public Transform DecorationsRoot;
        [HideInInspector] public GameObject[] GeneratedDecorations = Array.Empty<GameObject>();
    }
}

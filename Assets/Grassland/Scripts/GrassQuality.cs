using UnityEngine;


[CreateAssetMenu(fileName = "GrassQuality", menuName = "Grassland/Grass Quality", order = 1)]
public class GrassQuality : ScriptableObject
{
    public const float Inherit = -1.0f;
    public const int InheritCount = 0;

    [Header("LOD")]
    public float lodDistance = Inherit;

    public float lodBlend = Inherit;

    [Header("Culling")]
    public float cullRadius = Inherit;

    [Range(0, 256)] public int cellCount = InheritCount;
}

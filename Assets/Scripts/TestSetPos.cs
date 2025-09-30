using UnityEngine;

public class TestSetPos : MonoBehaviour
{
    public Vector3 _worldPosition;
    public float _worldRotationDegrees;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        var t = GetComponent<Transform2DBehaviour>();
        t.position = _worldPosition;
        t.rotationDegrees = _worldRotationDegrees;
    }
}

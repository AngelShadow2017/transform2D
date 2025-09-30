using UnityEngine;

public class TestAAA : MonoBehaviour
{
    public Transform2DBehaviour s,th;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        th.SetParent(s, true);
        Debug.Log("A: "+th.transform.localPosition);
        th.ForceSyncNow();
        Debug.Log("B: "+th.transform.localPosition);
        Debug.Log("C: "+th.transform.position);
    }
}

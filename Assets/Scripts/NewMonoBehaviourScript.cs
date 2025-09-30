using UnityEngine;

public class NewMonoBehaviourScript : MonoBehaviour
{
    public Transform2DBehaviour b1, child;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        child.SetParent(b1,true);
    }
}

using UnityEngine;
using DG.Tweening;

public class ShowFPS : MonoBehaviour
{
    //更新的时间间隔
    public float UpdateInterval = 0.5F;
// #if UNITY_EDITOR
    //最后的时间间隔
    private float _lastInterval;
    //帧[中间变量 辅助]
    private int _frames = 0;
    //当前的帧率
    private float _fps;
    private GUIStyle _titleStyle;

    private Rect _rect = new Rect(10, 10, 200, 200);


    void Start()
    {
   
        //UpdateInterval = Time.realtimeSinceStartup;
        _frames = 0;
        _titleStyle = new GUIStyle();
        _titleStyle.fontSize = 30;
        _titleStyle.normal.textColor = new Color(46f / 256f, 163f / 256f, 256f / 256f, 256f / 256f);
    }



    void OnGUI()
    {

        GUI.Label(_rect, "FPS:" + _fps.ToString("f2"), _titleStyle);
    }

    void Update()
    {
        ++_frames;

        if (Time.realtimeSinceStartup > _lastInterval + UpdateInterval)
        {
            _fps = _frames / (Time.realtimeSinceStartup - _lastInterval);

            _frames = 0;

            _lastInterval = Time.realtimeSinceStartup;
        }
    }
// #endif
}
/*
 * 文件名: WorldMiniMap.cs
 * 作者: zengxin
 * 创建日期: 2026-9-5
 */
using UnityEngine;

public class WorldMiniMap : MonoBehaviour
{
    [Tooltip("裁剪区（RectMask2D 所在节点，静态裁剪用，代码不驱动）")]
    [SerializeField] private RectTransform _viewportRect;

    [Tooltip("缩略图大图（world_minmap >10177×5088,可多做点，不然显示框会留空，localScale 跟主地图缩放、anchoredPosition 滑动定位）")]
    [SerializeField] private RectTransform _thumbRect;

    [Tooltip("屏幕指示框（固定居中不动、尺寸恒定 50×89，代表主视口；）")]
    [SerializeField] private RectTransform _viewRect;

    [Tooltip("content像素→缩略图基准比例尺 = n（缩略图基准尺寸 = content÷n）")]
    [SerializeField] private float _scale = 1f / 15f;

    /// <summary>
    /// 主视口同步（content 局部像素）。
    /// </summary>
    /// <param name="centerPx">视口中心 content 局部 X（已扣缩放，缩放围绕 content 中心故与 scale 无关）</param>
    /// <param name="centerPy">视口中心 content 局部 Y</param>
    /// <param name="mapScale">主地图当前缩放 content.localScale.x（0.6~1.0），作为缩略图 localScale</param>
    public void UpdateViewport(float centerPx, float centerPy, float mapScale)
    {
        if (_thumbRect == null)
            return;

        // 缩略图与主地图等比缩放（只设 xy，z 恒 1）
        float s = _scale * mapScale;
        Vector3 ls = _thumbRect.localScale;
        _thumbRect.localScale = new Vector3(mapScale, mapScale, ls.z);

        // 滑动定位：让 content(centerPx,centerPy) 的成像点落到窗口中心(=缩略图中心)。
        // 缩略图中心对应 content(0,0)；content 单位坐标成像 = 原坐标×s（localScale 绕中心，
        // 中心对齐不受缩放影响），故平移量 = -center×s 即可使该点移到窗口中心。
        _thumbRect.anchoredPosition = new Vector2(
            -centerPx * s,
            -centerPy * s);
    }
}

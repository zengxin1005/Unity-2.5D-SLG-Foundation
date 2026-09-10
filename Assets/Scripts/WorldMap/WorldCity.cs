/*
* 类    名：WorldCity.cs
* 作    者：zengxin
* 创建时间：2026-8-20
*/
using UnityEngine;

/*
 城池对象——纯表现层（WorldObject 子类，走 WorldObjectManager 对象池）。
          生灭由业务数据驱动：AddCity 创建 / RemoveCity 移除；
          视口滚动通过 SetupObject/DestroyObject 驱动城池的显示/回收（屏幕内复用）。
          位置为 cityLayer 层空间坐标（content 像素，由 TiledPositionToContentPoint 换算），
          与行军线端点同一坐标系，保证行军线从城中心发出
 */

[RequireComponent(typeof(SpriteRenderer))]
public class WorldCity : WorldObject
{
    private SpriteRenderer _renderer;

    public SpriteRenderer Renderer
    {
        get
        {
            if (_renderer == null)
                _renderer = GetComponent<SpriteRenderer>();
            return _renderer;
        }
    }

    /// <summary>
    /// 激活城池并放置到指定位置（层空间）
    /// </summary>
    /// <param name="sprite">城池精灵（null 时保留预制体自带贴图，供图集缺省时兜底）</param>
    /// <param name="sortingOrder">渲染层级（需大于地图 tile 的 0）</param>
    /// <param name="parent">父节点（cityLayer，不旋转）</param>
    /// <param name="localPos">层空间位置（content 像素）</param>
    /// <param name="size">渲染尺寸（层空间/content 像素；与纹理分辨率、PPU 解耦）</param>
    public void Show(
        Sprite sprite,
        int sortingOrder,
        Transform parent,
        Vector3 localPos,
        Vector2 size)
    {
        gameObject.SetActive(true);
        transform.SetParent(parent, false);
        transform.localPosition = localPos;

        if (sprite != null)
            Renderer.sprite = sprite;
        Renderer.sortingOrder = sortingOrder;

        // 与 MarchArrow 一致：SpriteRenderer 默认世界尺寸 = 纹理像素/PPU，与层空间单位不匹配，
        // 这里按 bounds 换算 localScale，使渲染尺寸恒等于配置值（美术改 PPU/换图不用调代码）。
        // bounds 各轴 < 0.0001 视为异常贴图，退化为 1 防止除零出 Infinity
        Vector3 bounds = Renderer.sprite != null ? Renderer.sprite.bounds.size : Vector3.zero;
        transform.localScale = new Vector3(
            bounds.x > 0.0001f ? size.x / bounds.x : size.x,
            bounds.y > 0.0001f ? size.y / bounds.y : size.y,
            1f);
    }

    /// <summary>
    /// 回收进池（WorldObjectManager.Despawn 调用，显示前由 Show 重新激活）
    /// </summary>
    public override void Recycle()
    {
        Renderer.sprite = null;
        transform.SetParent(null, false);
        gameObject.SetActive(false);
    }
}

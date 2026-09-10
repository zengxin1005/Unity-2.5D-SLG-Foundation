/*
 * 文件名: WorldMapController.cs
 * 作者: zengxin
 * 创建日期: 2026-7-27
 */
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Scx; 
public class WorldMapController : MonoBehaviour
{
    private Vector2 _contentSize;
    public TiledMapComponent tiledMapComponent;
    public WorldObjectManager worldObjectManager;
    public GestureScrollRect mapScroller;
    
    // 下面的参数用于回收有关的
    private Rect _range;
    private Rect _lastRange;
    private Dictionary<int, int> _lastIndexHash = new ();
    private Dictionary<int, int> _tmpIndexHash = new ();
    private Dictionary<int, int> _indexHash = new ();

    // 只由 OnValueChanged 维护（视口中心所在格），外部只读
    public int CenterTileX { get; private set; }
    public int CenterTileY { get; private set; }

    // 计算方向有关的
    private readonly MapMetaInfo _mapMetaInfo = new ();

    [Header("地图边缘缓冲格子数")]
    public int edgeBufferTiles  = 5;

    public WorldMapView MapView { get; set; }
    
    private void Start()
    {
        InitMap().Forget();
    }
    
    private async UniTaskVoid InitMap()
    {
        // X-UniTMX 挂父是世界保持语义（Map.cs 的 mapObjectTransform.parent = Parent.transform），
        // 构建期间父链缩放必须为 1，否则地图子树 localScale 会被补偿成 1/父缩放（如 1.42857）导致整图错位。
        // 先记下场景配置的初始缩放，构建期间归一，构建完成后再应用。
        float initialScale = mapScroller.content.localScale.x;
        mapScroller.content.localScale = Vector3.one;
        
        await tiledMapComponent.LoadMap("WorldMap/map.xml");
        // await 期间场景/本组件可能已被销毁（快速进出地图）：续体直接返回，
        // 防止对已销毁对象的后续操作抛 MissingReferenceException
        if (this == null)
            return;
        _mapMetaInfo.SetTiledMap(tiledMapComponent.TiledMap);
        
        mapScroller.OnValueChanged = OnValueChanged;
        mapScroller.OnScaleChange = OnScaleChanged;
        // 点选/用户操作（View 层联动）：
        // OnPressDown —— 地图任意位置"一按下"即通知 View 回收面板（不等待释放/拖动判定）；
        //               点城弹面板仍在 OnClick（释放且未拖动）命中城池后触发，天然形成"收旧→弹新"。
        // OnScaleChange —— 编辑器滚轮缩放没有"按下"，单独补一条收起路径（双指缩放第一指按下已覆盖）。
        mapScroller.OnClick = OnMapClick;
        mapScroller.OnPressDown = () => MapView?.OnMapPressDown();

        _contentSize = new Vector2(_mapMetaInfo.PixelWidth, _mapMetaInfo.PixelHeight);
        mapScroller.content.sizeDelta = _contentSize;
        
        worldObjectManager.Initialize(_mapMetaInfo);
        worldObjectManager.marchLayer.sizeDelta = _contentSize;
        worldObjectManager.cityLayer.sizeDelta = _contentSize;
        worldObjectManager.armyLayer.sizeDelta = _contentSize;
        
        tiledMapComponent.transform.localScale = new Vector3(_mapMetaInfo.TileWidth, _mapMetaInfo.TileWidth, 1);
        tiledMapComponent.transform.localPosition = new Vector3(
            -_contentSize.x / 2 - _mapMetaInfo.MarginWidthPixel + _mapMetaInfo.TileWidth / 2.0f,
            _contentSize.y / 2 + _mapMetaInfo.MarginHeightPixel + _mapMetaInfo.TileHeight / 2.0f,
            0
        );
        mapScroller.SetScale(initialScale);
        
        //test
        DeployTestData();
    }

    /// <summary>
    /// 测试数据：(300,300) 主城 1 座（Self）；以 (300,300) 为圆心、半径 20 格圆周均匀分布 100 座外围城（Ally），
    /// 100 座外围城全部向 (300,300) 主城发直线行军（Relation 随机 0-3，压测混色）；
    /// 另加 (250,250) 演示城 1 座 → 主城 (300,300) 的固定演示行军线（Ally，验证单条路径箭头）。
    /// </summary>
    private void DeployTestData()
    {
        const int centerX = 300;
        const int centerY = 300;
        const float radiusTiles = 20f;

        // 主城
        worldObjectManager.AddCity(new CityEntity
        {
            Id = 1L,
            TileX = centerX,
            TileY = centerY,
            Type = 0,
            Relation = MarchRelation.Self
        });

        // 圆周 100 城：Content 像素空间正圆均匀分布（视觉正圆），取整回瓦片坐标。
        // 循环次数必须等于城数（total=100）：若 > 城数，超出部分按同角度重复部署，
        // 后一轮 AddCity 同格挤掉先一轮城池，却留下重复路径的行军线 → 同路径箭头密度翻倍
        _mapMetaInfo.TiledPositionToContentPoint(centerX, centerY, out float centerPx, out float centerPy);
        float tileStep = Mathf.Sqrt(
            _mapMetaInfo.TileWidth * _mapMetaInfo.TileWidth +
            _mapMetaInfo.TileHeight * _mapMetaInfo.TileHeight) / 2f; // 相邻格中心距 ≈142px
        float radiusPx = radiusTiles * tileStep;

        int total = 100;
        for (int i = 0; i < total; i++)
        {
            float angle = i * Mathf.PI * 2f / total;
            float px = centerPx + Mathf.Cos(angle) * radiusPx;
            float py = centerPy + Mathf.Sin(angle) * radiusPx;
            _mapMetaInfo.ContentPointToTiledPosition(px, py, out int tileX, out int tileY);

            long id = 2L + i;
            worldObjectManager.AddCity(new CityEntity
            {
                Id = id,
                TileX = tileX,
                TileY = tileY,
                Type = 0,
                Relation = MarchRelation.Ally
            });
            // 本城 → 主城
            worldObjectManager.AddMarch(new MarchEntity
            {
                Id = id,
                StartTileX = tileX,
                StartTileY = tileY,
                EndTileX = centerX,
                EndTileY = centerY,
                Speed = 200f,
                Relation = (MarchRelation)Random.Range((int)MarchRelation.Self,(int)MarchRelation.Enemy + 1),
            });
        }
        
        // 演示城 (250,250) → 主城 (300,300)：单条固定行军线，验证"两城间路径箭头"。
        const int demoCityX = 250;
        const int demoCityY = 250;
        long demoId = total + 2L; // 102
        worldObjectManager.AddCity(new CityEntity
        {
            Id = demoId,
            TileX = demoCityX,
            TileY = demoCityY,
            Type = 0,
            Relation = MarchRelation.Ally
        });
        // 本城 → 主城折线（模拟绕路行军）非直线的任意路径的贴地行军线
        // 中间段 x 每 5~6 格递进、y 在 247~258 间交替摆动（方向 ↗↘ 交替，可直观验证每段箭头方向独立），
        // 后 3 点 (x≥293) 逐渐转向终点 (300,300)，模拟"绕完障碍后直奔目标"。整路径 x 单调递增不自交。
        var demoWaypoints = new[]//路径点
        {
            new Vector2Int(255, 253),
            new Vector2Int(260, 247),
            new Vector2Int(266, 254),
            new Vector2Int(271, 247),
            new Vector2Int(277, 254),
            new Vector2Int(282, 248),
            new Vector2Int(288, 254),
            new Vector2Int(293, 258),
            new Vector2Int(296, 272),
            new Vector2Int(299, 286),
        };
        worldObjectManager.AddMarch(new MarchEntity
        {
            Id = demoId,
            StartTileX = demoCityX,
            StartTileY = demoCityY,
            EndTileX = centerX,
            EndTileY = centerY,
            Waypoints = demoWaypoints,
            Speed = 200f,
            Relation = MarchRelation.Ally,
        });
    }
    
    
 

    
    private bool OnValueChanged()
    {
        mapScroller.ViewportToContent(out float pixelX, out float pixelY);
        float viewportW = mapScroller.viewport.rect.width / mapScroller.content.localScale.x;
        float viewportH = mapScroller.viewport.rect.height / mapScroller.content.localScale.x;
        float centerPx = pixelX + viewportW / 2;
        float centerPy = pixelY - viewportH / 2;
        _mapMetaInfo.ContentPointToTiledPosition(centerPx, centerPy, out int centerTileX, out int centerTileY);
        if (centerTileX < 0 || centerTileX >= _mapMetaInfo.TileCountPerWidth ||
            centerTileY < 0 || centerTileY >= _mapMetaInfo.TileCountPerHeight)
            return false;
        CenterTileX = centerTileX;
        CenterTileY = centerTileY;
        MapView?.SetMapCenter(CenterTileX,CenterTileY);
        // 小地图等视口订阅者：每次滚动/缩放都同步（视口中心 content 坐标 + 主地图缩放，
        // 后者作为缩略图 localScale，使固定屏幕框始终精确框住当前视口）
        MapView?.miniMap?.UpdateViewport(centerPx, centerPy, mapScroller.content.localScale.x);
        
        _mapMetaInfo.ContentPointToTiledPosition(pixelX, pixelY,out int tileX,out int tileY);
        _range.x = tileX - edgeBufferTiles;
        _range.y = tileY;
        _range.width = Mathf.Ceil(viewportW / _mapMetaInfo.TileWidth)+edgeBufferTiles;
        _range.height = Mathf.Ceil(viewportH / _mapMetaInfo.TileHeight)+edgeBufferTiles;
        if (!_range.Equals(_lastRange))
        {
            UpdateInsideTile();
            CollectOutsideTile();
            _lastRange = _range;
            // 逐格进出只标记脏线，此处统一重建可见段（避免滚动时每格全量重建箭头）
            worldObjectManager.FlushDirtyLines();
        }
        return true;
    }
    



    private void UpdateInsideTile()
    {
        int realXLoop = (int)_range.x;
        int realYLoop = (int)_range.y;
        int loopX = 0;
        int loopY = 0;

        while (true)
        {
            while (true)
            {
                if (_mapMetaInfo.IsTiledXYInRangeWithMargin(realXLoop, realYLoop))
                {
                    int indexWithMargin = _mapMetaInfo.TiledXYToIndexWithMargin(realXLoop, realYLoop);
                    if (!_indexHash.TryGetValue(indexWithMargin, out _))
                    {
                        _indexHash[indexWithMargin] = indexWithMargin;
                        _mapMetaInfo.TiledXYWithMargin(realXLoop, realYLoop, out int realXWithMargin,
                            out int realYWithMargin);
                        tiledMapComponent.TiledMap.SetupTile(realXWithMargin, realYWithMargin);
                        worldObjectManager.SetupObject(realXLoop, realYLoop);
                    }

                    _tmpIndexHash[indexWithMargin] = indexWithMargin;
                    _lastIndexHash.Remove(indexWithMargin);
                }

                if (_mapMetaInfo.IsTiledXYInRangeWithMargin(realXLoop, realYLoop - 1))
                {
                    int nextIndexWithMargin = _mapMetaInfo.TiledXYToIndexWithMargin(realXLoop, realYLoop - 1);
                    if (!_indexHash.TryGetValue(nextIndexWithMargin, out _))
                    {
                        _indexHash[nextIndexWithMargin] = nextIndexWithMargin;
                        _mapMetaInfo.TiledXYWithMargin(realXLoop, realYLoop - 1, out int realXWithMargin,
                            out int realYWithMargin);
                        tiledMapComponent.TiledMap.SetupTile(realXWithMargin, realYWithMargin);
                        worldObjectManager.SetupObject(realXLoop, realYLoop - 1);
                    }

                    _tmpIndexHash[nextIndexWithMargin] = nextIndexWithMargin;
                    _lastIndexHash.Remove(nextIndexWithMargin);
                }

                realXLoop += 1;
                loopX += 1;
                realYLoop -= 1;
                if (loopX >= _range.width) break;
            }

            if (loopY >= _range.height) break;

            loopX = 0;
            loopY += 1;
            realXLoop = (int)_range.x + loopY;
            realYLoop = (int)_range.y + loopY;
        }
    }
    


    private void CollectOutsideTile()
    {
        foreach (var kv in _lastIndexHash)
        {
            int index = kv.Key;
            _mapMetaInfo.TiledIndexToXYWithMargin(index, out int xWithMargin, out int yWithMargin);
            tiledMapComponent.TiledMap.DestroyTile(xWithMargin, yWithMargin);
            _mapMetaInfo.TiledXYWithoutMargin(xWithMargin,yWithMargin,out int tileX,out int tileY);
            worldObjectManager.DestroyObject(tileX, tileY);
            _indexHash.Remove(index);
        }
        _lastIndexHash.Clear();
        var tmpLast = _lastIndexHash;
        _lastIndexHash = _indexHash;
        _indexHash = _tmpIndexHash;
        _tmpIndexHash = tmpLast;
    }
    
    private void OnScaleChanged()
    {
        OnValueChanged();
#if UNITY_EDITOR
        // 编辑器滚轮缩放没有"按下"，单独补一条回收路径（真机双指缩放第一指按下已由 OnPressDown 覆盖）
        MapView?.OnMapPressDown();
#endif
    }
    
    /// <summary>
    /// 滚动到指定格，目标格最终落点可相对视口中心偏置。
    /// </summary>
    /// <param name="tileX">目标格 X（无边缘瓦片坐标）</param>
    /// <param name="tileY">目标格 Y</param>
    /// <param name="withAnimate">是否平滑滚动</param>
    /// <param name="contentOffsetY">让位量（content 像素，>0 = 城最终停在视口中心上方该像素处，底部面板让位用）。
    /// 实现：滚动瞄准的虚拟目标点取在城下方 contentOffsetY 处（content 局部 Y 向上为正，故 TransformPoint 前先减），
    /// ScrollToCenter 令虚拟点居中后，城自然落在中心上方 contentOffsetY 像素处；偏移量=让位世界高/2 再除当前缩放。</param>
    public bool ScrollToTile(int tileX, int tileY, bool withAnimate = false, float contentOffsetY = 0.0f)
    {
        if (tileX < 0 || tileX >= _mapMetaInfo.TileCountPerWidth ||
            tileY < 0 || tileY >= _mapMetaInfo.TileCountPerHeight)
            return false;
        /* 如果是横屏一直不需要偏移直接使用这个吧
        _mapMetaInfo.TiledPositionToWorldPointByContent(
            mapScroller.content, tileX, tileY, out float worldX, out float worldY);
        mapScroller.ScrollToCenterByWorldXYZ(worldX, worldY, mapScroller.content.position.z, withAnimate);
        */
        _mapMetaInfo.TiledPositionToContentPoint(tileX, tileY, out float px, out float py);
        // 城要显示在视口中心上方 → 瞄准点取城下方（py - 让位量）；content 局部→世界由 TransformPoint 按 localScale 换算
        Vector3 world = mapScroller.content.TransformPoint(new Vector3(px, py - contentOffsetY, 0f));
        mapScroller.ScrollToCenterByWorldXYZ(world.x, world.y, mapScroller.content.position.z, withAnimate);
        return true;
    }

    /// <summary>
    /// 地图轻点入口（GestureScrollRect.OnClick 回调，content 局部像素）。
    /// 命中城池 → 交给 View 弹出面板；点空白 → 交给 View（仅收起面板）。
    /// UI 交互决策全部在 View（它知道面板状态与高度），Controller 只做命中与滚动
    /// </summary>
    private void OnMapClick(float contentX, float contentY)
    {
        if (worldObjectManager == null || MapView == null)
            return;
        
        //当前你也可以通过其他方式相应城池 ，比如 1.Image+ PointerDown/PointClick 2.Button
        //3. 相机 Physics2DRaycaster/PhysicsRaycaster + Collider2D/Collider
        //4.  Collider2D/Collider + Physics2D.OverlapPoint 检测 5.甚至自身脚本OnMouse,这种只能是主相机
        //PickCityAt 可能挨的近层级会有问题，反正按照自己的需求吧
        ICityEntity city = worldObjectManager.PickCityAt(contentX, contentY);
        MapView.OnMapClicked(city);
    }
}
/*
*类    名：MarchLine.cs
*作    者：zengxin
*创建时间：2026-8-10
*/
using System;
using System.Collections.Generic;
using UnityEngine;
/*
行军线管理器——纯 C# 普通对象（非 MonoBehaviour、不参与对象池）。
生灭完全由业务数据控制（AddMarch 创建 / RemoveMarch 丢弃），
与视口滚动无关；屏幕滚动只驱动可见段（箭头槽位集合）的变化。
- 职责：维护"格子→轨道区间"映射（manager 采样生成），
汇总在屏格子的区间并集得到可见段，段内分配箭头槽位
MarchLine.Tick 每帧按流速推进各槽位相位并刷新渲染位置，
ArrowMeshBatcher 每帧收集全部槽位合并为一张 Mesh 绘制
（按贴图纹理分 submesh，DrawCall = 纹理桶数，万级目标个位数）
- 滚动增量：格子进出屏只维护 _tilesInView 集合（O(1)），
帧末由宿主 FlushDirtyLines 统一 RebuildSlots（不做相位锚定，
只增删槽位数据，无对象生灭；历史相位锚定方案已被否决）
- 回收：Dispose()（RemoveMarch 调用，清引用）
 */

/// <summary>
/// SegStart/SegLen 定义槽位所在的直线子段（累计弧长）；Phase 为段内相位
/// （0..1，Tick 按流速推进，越界折返段首）；Pos 为当前渲染位置（Tick 每帧更新）。
/// 槽位集合变化（格子进出屏）时整表重排（不做相位锚定）
/// </summary>
public struct ArrowSlot
{
    public float SegStart;   // 槽位所在直线子段起点（累计弧长）
    public float SegLen;     // 子段弧长（>0，箭头在该区间内循环流动）
    public float Phase;      // 段内相位 0..1（Tick 推进，越界折返段首）
    public float Angle;      // 箭头朝向（度，含贴图补偿角；子段方向恒定）
    public Sprite Sprite;    // 箭头贴图（合并渲染按 texture 分桶）
    public Vector3 Pos;      // 渲染位置（Tick 每帧由 TrackToPoint 更新）
}

public class MarchLine
{
    private IMarchEntity _entity;
    private IMarchHost _host;

    // 折线几何：层空间路点（含起终点）+ 累计弧长表 + 总长。
    // t 的语义为"沿折线的累计弧长"：定位按弧长查段、段内插值（直线时退化为单段）
    private Vector2[] _polyline;
    private float[] _cumLength;        // _cumLength[i] = 第 i 个路点处的累计弧长（0.._totalLength）
    private float _totalLength;

    private Vector2 _arrowSize;
    private float _arrowSpacing;
    private float _spriteAngleOffset;

    // 当前箭头槽位（与可见段一一对应；由 RebuildSlots 整表重排，不做相位锚定）
    private readonly List<ArrowSlot> _slots = new List<ArrowSlot>();
    // 当前处于可视范围内的相关格子（无边缘索引，与 WorldObjectManager._inRangeTiles 同编码）
    private readonly HashSet<int> _tilesInView = new HashSet<int>();
    // 格子（无边缘索引）-> 轨道区间列表 [t0, t1]（沿折线的累计弧长；一格可属多段）。
    // 直接持有 WorldObjectManager 传入的引用：AddMarch 每次传入全新字典（本线独占、无外部别名），
    // 深拷贝纯属浪费，Dispose 时置空即可
    private Dictionary<int, List<Vector2>> _tileTrackRanges = new Dictionary<int, List<Vector2>>();

    // 可见段合并的复用缓冲（排序后原地合并，结果在 [0, segmentCount)）
    private readonly List<Vector2> _segments = new List<Vector2>();
    // 区间起点排序比较器：静态缓存（避免 Sort 每次构建委托）
    private static readonly Comparison<Vector2> SegmentStartCompare = (a, b) => a.x.CompareTo(b.x);
    // 可见段按拐点切子段的复用缓冲
    private readonly List<Vector2> _subSegments = new List<Vector2>();

    /// <summary>当前箭头槽位（合并渲染器只读遍历；同程序集内部契约，勿外部修改）</summary>
    public List<ArrowSlot> Slots => _slots;

    /// <summary>是否有在屏箭头（宿主据此维护活跃线列表）</summary>
    public bool HasSlots => _slots.Count > 0;

    /// <summary>箭头渲染尺寸（合并渲染器生成顶点用）</summary>
    public Vector2 ArrowSize => _arrowSize;

    /// <summary>
    /// 设置/刷新行军数据与几何（首次创建与覆盖式刷新共用，幂等）。
    /// polyline：折线路点（层空间，含起终点；直线时即 [start, end] 两点）。
    /// tileTrackRanges：格子→轨道区间列表（WorldObjectManager.CalcMarchTiles 采样生成，
    /// t 为累计弧长，一格可属多段）。
    /// 注意：本方法只更新数据与几何，不重建槽位——屏内状态由调用方紧随其后的
    /// RefreshViewState + 标记脏（帧末 FlushDirtyLines 统一 RebuildSlots）
    /// </summary>
    public void Refresh(
        IMarchHost host,
        IMarchEntity entity,
        Vector2[] polyline,
        Vector2 arrowSize,
        float arrowSpacing,
        float spriteAngleOffset,
        Dictionary<int, List<Vector2>> tileTrackRanges)
    {
        _host = host;
        _entity = entity;
        _arrowSize = arrowSize;
        _arrowSpacing = arrowSpacing;
        _spriteAngleOffset = spriteAngleOffset;

        _tileTrackRanges = tileTrackRanges ?? new Dictionary<int, List<Vector2>>();

        BuildPolylineGeometry(polyline);
    }

    /// <summary>
    /// 相关格子进入可视范围（WorldObjectManager.SetupObject 通知）。
    /// 只维护在屏格子集合（O(1) 幂等），可见段重建由帧末 FlushDirtyLines 统一执行——
    /// 滚动帧内逐格通知不再触发全量重建，消除"每格全量重建"的滚动卡顿
    /// </summary>
    public void NotifyTileIn(int tileIndex)
    {
        _tilesInView.Add(tileIndex);
    }

    /// <summary>
    /// 相关格子离开可视范围（WorldObjectManager.DestroyObject 通知）。
    /// 只维护在屏格子集合（O(1) 幂等），可见段重建由帧末 FlushDirtyLines 统一执行
    /// </summary>
    public void NotifyTileOut(int tileIndex)
    {
        _tilesInView.Remove(tileIndex);
    }

    /// <summary>
    /// 整表同步屏内状态（行军创建/覆盖刷新后调用，重算交集；槽位重建由宿主统一调度）
    /// </summary>
    public void RefreshViewState(HashSet<int> marchTiles, HashSet<int> inRangeTiles)
    {
        _tilesInView.Clear();
        foreach (int tileIndex in marchTiles)
        {
            if (inRangeTiles.Contains(tileIndex))
                _tilesInView.Add(tileIndex);
        }
    }

    /// <summary>
    /// 重建箭头槽位（整表重排）：在屏格子区间 → 排序合并为互不相交的可见段 → 段内布槽位。
    /// 由宿主 FlushDirtyLines 帧末统一调用；槽位是纯数据（无 GameObject 生灭），
    /// 箭头流动完全由 Tick 按相位驱动，段变化不会产生对象级重建开销。
    /// 注意：整表重排不做相位锚定（滚动时箭头位置允许重置均布——历史方案已被否决）
    /// </summary>
    public void RebuildSlots()
    {
        _slots.Clear();

        if (_tilesInView.Count == 0 || _entity == null || _host == null)
            return;

        Sprite sprite = _host.GetArrowSprite(_entity.Relation);
        if (sprite == null)
            return;

        // 1. 收集在屏格子的轨道区间（一格可能属多段，逐区间收集）
        _segments.Clear();
        foreach (int tileIndex in _tilesInView)
        {
            if (_tileTrackRanges.TryGetValue(tileIndex, out List<Vector2> ranges))
            {
                for (int r = 0; r < ranges.Count; r++)
                    _segments.Add(ranges[r]);
            }
        }
        if (_segments.Count == 0)
            return;

        // 2. 按区间起点排序，原地合并重叠/相邻区间为互不相交的可见段。
        //    单区间无需排序（最常见：线只压到 1 个格子的可见段），跳过 Sort 省 O(k log k)
        if (_segments.Count > 1)
            _segments.Sort(SegmentStartCompare);
        int mergeIndex = 0;
        for (int i = 1; i < _segments.Count; i++)
        {
            if (_segments[i].x <= _segments[mergeIndex].y + 0.01f)
            {
                if (_segments[i].y > _segments[mergeIndex].y)
                    _segments[mergeIndex] = new Vector2(_segments[mergeIndex].x, _segments[i].y);
            }
            else
            {
                mergeIndex++;
                _segments[mergeIndex] = _segments[i];
            }
        }
        int segmentCount = mergeIndex + 1;

        // 3. 段内布槽位：每段数量 = 段长/步距取整（至少 1），全线上限 MaxArrowCount。
        //    段可能跨拐点（折线）：先按累计弧长切成直线子段，箭头只在子段内直线流动
        float step = _arrowSize.x + _arrowSpacing;
        int maxCount = _host.MaxArrowCount;
        int total = 0;

        for (int s = 0; s < segmentCount && total < maxCount; s++)
        {
            float segStart = _segments[s].x;
            float segEnd = _segments[s].y;

            SplitByCorners(segStart, segEnd, _subSegments);
            for (int sub = 0; sub < _subSegments.Count && total < maxCount; sub++)
            {
                float subStart = _subSegments[sub].x;
                float subEnd = _subSegments[sub].y;
                float subLength = Mathf.Max(0.01f, subEnd - subStart);

                int count = Mathf.Clamp(Mathf.FloorToInt(subLength / step), 1, maxCount - total);
                float subAngle = AngleAt(subStart); // 子段方向 + 贴图补偿角
                for (int i = 0; i < count; i++)
                {
                    // 子段内均匀分布，相位错开；Tick 各自流向子段末后重置回子段首循环
                    float phase = count > 1 ? (float)i / count : 0.5f;
                    _slots.Add(new ArrowSlot
                    {
                        SegStart = subStart,
                        SegLen = subLength,
                        Phase = phase,
                        Angle = subAngle,
                        Sprite = sprite,
                        Pos = TrackToPoint(subStart + subLength * phase)
                    });
                    total++;
                }
            }
        }
    }

    /// <summary>
    /// 每帧驱动（宿主 LateUpdate 只遍历活跃线）：按流速推进各槽位相位，
    /// 刷新渲染位置。等价于旧实现"tween 子段内点对点流动、终点重置段首"，
    /// 但无 tween 对象、无 GC 分配
    /// </summary>
    public void Tick(float dt)
    {
        int n = _slots.Count;
        if (n == 0 || _entity == null)
            return;

        float speed = _entity.Speed;
        // NaN/Infinity/非正速防御：非法速度会让 Phase 变 NaN → TrackToPoint → mesh 顶点 NaN → 花屏。
        // !(NaN > 0) 为 true，一个比较同时拦下 NaN/±Infinity/0/负速
        if (!(speed > 0f))
            return;
        List<ArrowSlot> slots = _slots;
        for (int i = 0; i < n; i++)
        {
            ArrowSlot slot = slots[i];
            if (slot.SegLen <= 0.01f)
                continue;

            slot.Phase += dt * speed / slot.SegLen;
            if (slot.Phase >= 1f)
                slot.Phase -= Mathf.Floor(slot.Phase); // 折返段首（一次减法恒落回 [0,1)）
            slot.Pos = TrackToPoint(slot.SegStart + slot.Phase * slot.SegLen);
            slots[i] = slot; // struct 列表需写回
        }
    }

    /// <summary>
    /// 丢弃前释放（RemoveMarch 调用）：清槽位与引用（槽位是纯数据，无对象需回收）
    /// </summary>
    public void Dispose()
    {
        _slots.Clear();
        _tilesInView.Clear();
        _tileTrackRanges = null;
        _polyline = null;
        _cumLength = null;
        _entity = null;
        _host = null;
    }

    /// <summary>
    /// 建立折线几何：路点数组 → 累计弧长表。
    /// 直线输入（2 点）时 _cumLength = [0, dist]，FindSegment 恒返回 0，与旧直线模型等价
    /// </summary>
    private void BuildPolylineGeometry(Vector2[] polyline)
    {
        _polyline = polyline ?? new Vector2[0];
        int n = _polyline.Length;
        if (n < 2)
        {
            _cumLength = new float[] { 0f, 0f };
            _totalLength = 0f;
            return;
        }

        _cumLength = new float[n];
        for (int i = 1; i < n; i++)
        {
            _cumLength[i] = _cumLength[i - 1]
                + (_polyline[i] - _polyline[i - 1]).magnitude;
        }
        _totalLength = _cumLength[n - 1];
    }

    /// <summary>累计弧长 t → 所在折线段索引（段数少，顺序扫即可）</summary>
    private int FindSegment(float t)
    {
        if (_cumLength == null || _cumLength.Length < 2)
            return 0;
        for (int i = 0; i < _cumLength.Length - 1; i++)
        {
            if (t <= _cumLength[i + 1])
                return i;
        }
        return _cumLength.Length - 2;
    }

    /// <summary>累计弧长 t → 层空间坐标（按弧长查段，段内线性插值）</summary>
    private Vector3 TrackToPoint(float t)
    {
        if (_polyline == null || _polyline.Length < 2)
            return Vector3.zero;
        t = Mathf.Clamp(t, 0f, _totalLength);
        int seg = FindSegment(t);
        float segLen = _cumLength[seg + 1] - _cumLength[seg];
        float k = segLen > 0.001f ? (t - _cumLength[seg]) / segLen : 0f;
        return Vector3.Lerp(_polyline[seg], _polyline[seg + 1], k);
    }

    /// <summary>
    /// 累计弧长 t → 箭头朝向角（所在折线段的方向 + 贴图补偿角）。
    /// 注意：t 恰好落在拐点上时按"拐点后一段"取方向（+0.001 偏移），
    /// 保证拐点处出发的子段箭头朝向正确
    /// </summary>
    private float AngleAt(float t)
    {
        if (_polyline == null || _polyline.Length < 2)
            return _spriteAngleOffset;
        int seg = FindSegment(Mathf.Min(t + 0.001f, _totalLength));
        Vector2 d = _polyline[seg + 1] - _polyline[seg];
        return Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg + _spriteAngleOffset;
    }

    /// <summary>
    /// 可见段 [a, b] 按拐点（累计弧长）切成互不跨拐点的直线子段，写入 output。
    /// 折线跨拐点时箭头只在子段内直线流动（不切角）；直线输入无拐点，原样返回整段
    /// </summary>
    private void SplitByCorners(float a, float b, List<Vector2> output)
    {
        output.Clear();
        float cur = a;
        for (int i = 0; i < _cumLength.Length - 2; i++)
        {
            float corner = _cumLength[i + 1];
            // 用 cur 而非 a 判断：重复路点会产生零长度段（拐点值相等），
            // 若 corner == cur 再切会得到零长度子段（多一个静止箭头），故排除
            if (corner > cur + 0.001f && corner < b - 0.001f)
            {
                output.Add(new Vector2(cur, corner));
                cur = corner;
            }
        }
        output.Add(new Vector2(cur, b));
    }
}

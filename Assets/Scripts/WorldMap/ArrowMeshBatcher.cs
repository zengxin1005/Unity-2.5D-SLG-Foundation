/*
* 类    名：ArrowMeshBatcher.cs
* 作    者：zengxin
* 创建时间：2026-08-01 
*/
/*

行军箭头合并渲染器（万级行军目标终局渲染路径）。
          - 所有在屏箭头的槽位数据（位置/角度/贴图/尺寸）每帧合并进一张 Mesh，
            按贴图纹理分 submesh（每纹理一个 Sprites/Default 材质），
            DrawCall = 纹理桶数（箭头贴图通常 1-4 张，万级箭头也是个位数）
          - 材质 renderQueue = 3001（大于 SpriteRenderer 默认 3000），保证
            在地图 tile（sortingOrder=0）之后绘制；同时挂 marchLayer 并设
            sortingOrder=10000（与城池同层，箭头为动态元素盖住静态对象）
          - 顶点/UV/三角数组预分配复用（容量翻倍扩容），每帧零分配；
            材质只按纹理惰性创建一次（_bucketIndex 持久不清理）
          - 由 WorldObjectManager.LateUpdate 每帧驱动（顺序：FlushDirtyLines
            → 各线 Tick 推进位置 → Render 收集合并）
*/
using System.Collections.Generic;
using UnityEngine;

public class ArrowMeshBatcher
{
    private Mesh _mesh;
    private MeshFilter _filter;
    private MeshRenderer _renderer;

    // 纹理桶：tex -> 桶索引（持久，材质只创建一次；桶计数每帧重置）
    private readonly Dictionary<Texture2D, int> _bucketIndex = new Dictionary<Texture2D, int>();
    private readonly List<Material> _materials = new List<Material>();
    private readonly List<int> _bucketCounts = new List<int>();  // 每帧每桶箭头数
    private readonly List<int> _validBuckets = new List<int>();  // 每帧 count>0 的桶（submesh 列表）
    private int[] _bucketTriStart = new int[4];                  // 每桶三角索引起点（submesh 布局）
    private int[] _lastBucketSeq = new int[0];                   // 上帧材质桶序列（长度=桶数；逐位比较，数量相同但桶集合变了也重建 → 防错色花屏）
    private Material[] _matArr = new Material[0];                // 材质数组复用缓冲（容量必须恒 == 桶数，否则 sharedMaterials 长度失配 → 不渲染）

    // 顶点缓冲（4 顶点 + 6 索引/箭头，容量翻倍扩容，复用避免每帧 GC）
    private Vector3[] _vertices = new Vector3[4096];
    private Vector2[] _uvs = new Vector2[4096];
    private int[] _triangles = new int[6144];
    private int _lastVertexCount; // 上帧提交顶点数（顶点收缩时先清旧索引再提交，防 SetVertices 校验失败）

    // 箭头材质 shader（Initialize 时解析一次并缓存：免热路径重复 Find；
    // 若构建期被资源裁剪则在初始化时 LogError 显式暴露，而非运行中箭头静默隐形）
    private Shader _spriteShader;

    /// <summary>
    /// 创建合并渲染器：1 个 GameObject + MeshFilter + MeshRenderer，挂在行军层下
    /// </summary>
    public void Initialize(Transform parent, int sortingOrder)
    {
        var go = new GameObject("ArrowMeshBatcher");
        go.transform.SetParent(parent, false);
        _filter = go.AddComponent<MeshFilter>();
        _renderer = go.AddComponent<MeshRenderer>();
        _renderer.sortingOrder = sortingOrder;

        // shader 构建期保险：Sprites/Default 平时随 SpriteRenderer 进包，但若某天工程内
        // 再无 SpriteRenderer 引用它而被资源裁剪，运行时 Shader.Find 返回 null →
        // 箭头整体静默隐形。这里在初始化时解析并缓存一次，缺失立即 LogError 暴露，
        // 修复方式：Project Settings → Graphics → Always Included Shaders 加入该 shader。
        _spriteShader = Shader.Find("Sprites/Default");
        if (_spriteShader == null)
            Debug.LogError("[ArrowMeshBatcher] 找不到 Sprites/Default shader（可能被构建裁剪），箭头将不渲染。请将其加入 Always Included Shaders。");

        _mesh = new Mesh { name = "MarchArrowMesh" };
        _mesh.MarkDynamic(); // 每帧更新顶点，标记动态避免 CPU 端拷贝缓存
        _filter.sharedMesh = _mesh;
        _renderer.enabled = false;
    }

    /// <summary>
    /// 丢弃前释放（WorldObjectManager.OnDestroy 调用）：显式销毁运行时创建的
    /// Mesh 与材质 —— 这两者不是场景对象，不随场景卸载自动销毁，
    /// 不释放的话 WorldMap 每次重复加载都会累积泄漏一套 mesh+材质。
    /// </summary>
    public void Dispose()
    {
        if (_filter != null)
            _filter.sharedMesh = null;
        if (_mesh != null)
        {
            UnityEngine.Object.Destroy(_mesh);
            _mesh = null;
        }
        for (int i = 0; i < _materials.Count; i++)
        {
            if (_materials[i] != null)
                UnityEngine.Object.Destroy(_materials[i]);
        }
        _materials.Clear();
        _bucketIndex.Clear();
        _bucketCounts.Clear();
        _validBuckets.Clear();
        _lastBucketSeq = new int[0];
        _matArr = new Material[0];
        if (_renderer != null)
            UnityEngine.Object.Destroy(_renderer.gameObject);
        _renderer = null;
        _filter = null;
    }

    /// <summary>
    /// 每帧收集活跃线的全部箭头槽位 → 合并写进一张 Mesh。
    /// 第一遍分桶统计，第二遍按桶连续写顶点/UV/三角（每桶一个连续 submesh 段）
    /// </summary>
    public void Render(List<MarchLine> lines)
    {
        if (lines == null || lines.Count == 0)
        {
            if (_renderer.enabled)
                _renderer.enabled = false;
            return;
        }

        // 1. 分桶统计（纹理种类固定，_bucketCounts 与 _materials 同步对齐）。
        //    线内所有槽位贴图相同（RebuildSlots 统一赋同一 sprite），按线首槽判断即可
        for (int b = 0; b < _bucketCounts.Count; b++)
            _bucketCounts[b] = 0;

        int total = 0;
        for (int li = 0; li < lines.Count; li++)
        {
            List<ArrowSlot> slots = lines[li].Slots;
            if (slots.Count == 0)
                continue;
            Sprite spr = slots[0].Sprite;
            if (spr == null || spr.texture == null)
                continue;
            int bucket = GetBucket(spr.texture);
            if (bucket < 0)
                continue;
            _bucketCounts[bucket] += slots.Count;
            total += slots.Count;
        }

        if (total == 0)
        {
            if (_renderer.enabled)
                _renderer.enabled = false;
            return;
        }
        if (!_renderer.enabled)
            _renderer.enabled = true;

        _validBuckets.Clear();
        for (int b = 0; b < _bucketCounts.Count; b++)
        {
            if (_bucketCounts[b] > 0)
                _validBuckets.Add(b);
        }
        int bucketCount = _validBuckets.Count;
        if (_bucketTriStart.Length < bucketCount)
            System.Array.Resize(ref _bucketTriStart, Mathf.Max(4, bucketCount * 2));

        EnsureCapacity(total * 4);

        // 2. 按桶收集写顶点/UV/三角（每桶连续段 → 直接映射 submesh 区间）
        int vi = 0;
        int ti = 0;
        for (int bi = 0; bi < bucketCount; bi++)
        {
            int bucket = _validBuckets[bi];
            Texture2D tex = _materials[bucket].mainTexture as Texture2D;
            _bucketTriStart[bi] = ti;

            for (int li = 0; li < lines.Count; li++)
            {
                MarchLine line = lines[li];
                List<ArrowSlot> slots = line.Slots;
                if (slots.Count == 0)
                    continue;
                // 线级贴图唯一：线内所有槽位同一 sprite，按线首槽判断归属桶
                if (slots[0].Sprite == null || slots[0].Sprite.texture != tex)
                    continue;
                Vector2 size = line.ArrowSize;
                float hw = size.x * 0.5f;
                float hh = size.y * 0.5f;
                for (int si = 0; si < slots.Count; si++)
                    WriteQuad(slots[si], slots[si].Sprite, tex, hw, hh, ref vi, ref ti);
            }
        }

        // 3. 提交 mesh（顶点每帧必然变；submesh 结构与材质数组只在布局变化时更新）。
        //    顶点收缩（滑动时箭头减少）时，mesh 内还残留上帧的大 triangles，
        //    其引用索引可能超过本帧新顶点数 → SetVertices 校验失败
        //    （"Mesh.vertices is too small"）→ 必须先 Clear 清空旧索引再提交；
        //    顶点增长/持平则走常规路径（保留 MarkDynamic 的 CPU 缓存复用）
        if (vi < _lastVertexCount)
        {
            _mesh.Clear();
            _mesh.subMeshCount = bucketCount; // Clear 重置了 submesh 结构，直接重建
        }
        else if (_mesh.subMeshCount != bucketCount)
        {
            _mesh.subMeshCount = bucketCount;
        }
        _mesh.SetVertices(_vertices, 0, vi);
        _mesh.SetUVs(0, _uvs, 0, vi);
        for (int bi = 0; bi < bucketCount; bi++)
        {
            int triStart = _bucketTriStart[bi];
            int triCount = (bi < bucketCount - 1 ? _bucketTriStart[bi + 1] : ti) - triStart;
            if (triCount > 0)
                _mesh.SetTriangles(_triangles, triStart, triCount, bi);
        }
        _lastVertexCount = vi;

        // 4. 材质绑定：桶序列逐位比较（数量或桶集合任一变化 → 重建材质数组）。
        //     材质数组长度必须恒等于 bucketCount（== subMeshCount），否则 Unity 拒绝绑定 → 整批不渲染；
        //     状态更新（_lastBucketSeq）必须放在最后，即使中途异常下帧也会重新比较重试。
        bool seqChanged = _lastBucketSeq.Length != bucketCount;
        for (int bi = 0; !seqChanged && bi < bucketCount; bi++)
            seqChanged = _lastBucketSeq[bi] != _validBuckets[bi];

        if (seqChanged)
        {
            if (_matArr.Length != bucketCount)          // 容量与桶数不一致 → 重建（不扩不缩，恒等匹配）
                _matArr = new Material[bucketCount];
            for (int bi = 0; bi < bucketCount; bi++)
                _matArr[bi] = _materials[_validBuckets[bi]];
            _renderer.sharedMaterials = _matArr;        // 数组长度恒 == subMeshCount

            if (_lastBucketSeq.Length != bucketCount)
                _lastBucketSeq = new int[bucketCount];
            for (int bi = 0; bi < bucketCount; bi++)
                _lastBucketSeq[bi] = _validBuckets[bi];
        }
    }

    /// <summary>
    /// 写入一个箭头的 quad：4 顶点（绕 Pos 旋转角度）+ 4 UV（sprite 在图集中的矩形）+ 6 索引
    /// </summary>
    private void WriteQuad(ArrowSlot slot, Sprite spr, Texture2D tex,
        float hw, float hh, ref int vi, ref int ti)
    {
        Vector3 pos = slot.Pos;
        float angle = slot.Angle * Mathf.Deg2Rad;
        float cosA = Mathf.Cos(angle);
        float sinA = Mathf.Sin(angle);

        // 局部角点 (-hw,-hh)(hw,-hh)(hw,hh)(-hw,hh) 绕 pos 旋转
        float r00 = cosA, r01 = -sinA, r10 = sinA, r11 = cosA;
        _vertices[vi]     = new Vector3(pos.x + r00 * -hw + r01 * -hh, pos.y + r10 * -hw + r11 * -hh, 0f);
        _vertices[vi + 1] = new Vector3(pos.x + r00 * hw + r01 * -hh,  pos.y + r10 * hw + r11 * -hh,  0f);
        _vertices[vi + 2] = new Vector3(pos.x + r00 * hw + r01 * hh,   pos.y + r10 * hw + r11 * hh,   0f);
        _vertices[vi + 3] = new Vector3(pos.x + r00 * -hw + r01 * hh,  pos.y + r10 * -hw + r11 * hh,  0f);

        // UV：sprite 在纹理（可能为图集）中的矩形，归一化到 [0,1]
        Rect r = spr.textureRect;
        float tw = tex.width;
        float th = tex.height;
        float u0 = r.xMin / tw;
        float u1 = r.xMax / tw;
        float v0 = r.yMin / th;
        float v1 = r.yMax / th;
        _uvs[vi]     = new Vector2(u0, v0);
        _uvs[vi + 1] = new Vector2(u1, v0);
        _uvs[vi + 2] = new Vector2(u1, v1);
        _uvs[vi + 3] = new Vector2(u0, v1);

        _triangles[ti]     = vi;
        _triangles[ti + 1] = vi + 1;
        _triangles[ti + 2] = vi + 2;
        _triangles[ti + 3] = vi;
        _triangles[ti + 4] = vi + 2;
        _triangles[ti + 5] = vi + 3;

        vi += 4;
        ti += 6;
    }

    /// <summary>
    /// 取（或惰性创建）某纹理的桶索引；材质按需创建一次并持久缓存。
    /// 返回 -1 表示无可用 shader（该纹理箭头跳过，防御）
    /// </summary>
    private int GetBucket(Texture2D tex)
    {
        if (_bucketIndex.TryGetValue(tex, out int idx))
            return idx;

        // shader 在 Initialize 时解析缓存（缺失已 LogError），此处直接用
        Shader shader = _spriteShader;
        if (shader == null)
            return -1;

        var mat = new Material(shader)
        {
            name = "MarchArrow_" + tex.name,
            mainTexture = tex,
            // renderQueue > 透明(3000)：在所有 SpriteRenderer（tile/城池）之后绘制，
            // 保证箭头盖在地图之上（MeshRenderer 的 sortingOrder 只排同类，跨渲染器按 queue）
            renderQueue = 3001
        };

        idx = _materials.Count;
        _materials.Add(mat);
        _bucketCounts.Add(0);
        _bucketIndex[tex] = idx;
        return idx;
    }

    /// <summary>顶点缓冲容量翻倍扩容（三角索引 = 顶点数 * 1.5）</summary>
    private void EnsureCapacity(int vertexCount)
    {
        if (vertexCount <= _vertices.Length)
            return;
        int newCap = Mathf.Max(vertexCount, _vertices.Length * 2);
        System.Array.Resize(ref _vertices, newCap);
        System.Array.Resize(ref _uvs, newCap);
        System.Array.Resize(ref _triangles, newCap + newCap / 2);
    }
}

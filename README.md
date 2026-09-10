<img width="1111" height="685" alt="a2352d2140b32b25f150be00b3758a49" src="https://github.com/user-attachments/assets/d4f7bdde-c86a-43e8-815f-54d93d91b902" />高性能2.5D SLG基础框架，基于视口渲染与缓冲池机制，实现万级单位同屏流畅渲染及计算。框架内置直线/自定义路径行军线,支持任意轨迹（贴地/飞行等）、城池等丰富基础元素，并集成完整手势交互系统，为超大地图下的策略体验提供稳定高效的底层支撑。

1.可以跟自己的UI框架，资源系统结合，自定义加载方式，目前采用UniTask异步加载，地图支持2D/2.5D，地图物件支持2D/3D。

2.基于 Tile 空间索引的视口驱动渲染（Viewport-Driven Rendering）——渲染与计算开销只与“可视区域 + 边缘缓冲”成正比，与地图总规模完全解耦，理论上支持无限大地图。

(1) 视口剔除（Viewport Culling）
滚动/缩放事件驱动格子（Tile）生命周期：只对进入“屏幕 + 边缘缓冲区”的格子做实例化，离开即回收。内存中永远只持有视口内的渲染对象。

(2)  行军线按需采样
支持直线与任意折线路径（路径绘制与寻路网格解耦，采样通用）；采样与箭头布点只针对视口内可见片段计算，屏幕外的路径不产生任何开销。

(3)   物件层同构复用
城池等物件与行军线共用同一套“视口驱动 + 对象池”机制，进出屏由格子生命周期统一调度，同屏渲染对象数量恒定在视口规模内。

(4)  对象池复用（Object Pooling）
箭头、城池等渲染物全部走通用对象池：出屏回收、进屏复用，运行期零 Instantiate/Destroy 抖动，无 GC 尖峰。

(5)  计算同样视口裁剪
所有坐标换算、路径采样、区间合并、显隐判定只在屏幕 + 缓冲范围内进行，屏幕外一律剔除。

单帧渲染与计算成本 = O(视口 + 缓冲)，与地图尺寸、实体总量无关。理论上地图可无限大，同屏可承载万级行军线 / 城池。系统性能上限不由引擎决定，而由内容侧决定——即数据总量（服务端/配置可承载的实体规模）与美术资源种类数。

<img width="642" height="1182" alt="img_v3_02155_893b41f6-2013-442f-ad50-96f208d3a8dg" src="https://github.com/user-attachments/assets/5d5384f4-acea-4490-a846-42eba46ea9b7" />

<img width="834" height="852" alt="img_v3_02156_0bd275bf-6274-4547-9fcf-f175aa2a5d0g" src="https://github.com/user-attachments/assets/068f377a-c473-4ab5-90b6-81ac39407713" />

<img width="761" height="734" alt="img_v3_02156_0e8b763a-8b31-4f3e-87a6-b0f22b314d3g" src="https://github.com/user-attachments/assets/08b732b0-e17d-4885-b30c-edd3ec52fe25" />

<img width="783" height="803" alt="img_v3_02156_e38e3517-e3e5-4aae-b81e-de8318c942fg" src="https://github.com/user-attachments/assets/8761a30d-e50e-4cde-86fb-b4561e2439f4" />

<img width="680" height="1062" alt="img_v3_0215b_198a24f3-1064-44e6-8753-7799e4e19f0g" src="https://github.com/user-attachments/assets/633cbb70-7c86-476b-b77b-aa1c53d7286f" />

<img width="596" height="1047" alt="img_v3_0215b_8b83daca-7a0f-403d-8c34-c7e70d2ac6cg" src="https://github.com/user-attachments/assets/c90f011e-69c8-4fac-800e-ea8d5df1db3e" />



<img width="1128" height="778" alt="062f36ab79e191c954497bedad5610b4" src="https://github.com/user-attachments/assets/2244164f-2b6a-476a-aa2f-bb0a87843b86" />
<img width="1111" height="685" alt="a2352d2140b32b25f150be00b3758a49" src="https://github.com/user-attachments/assets/a847fed5-aab6-4567-a03a-db0b70bd43e9" />
<img width="1205" height="925" alt="8b4ab2b11dd29c5a75acf8d37979dfd8" src="https://github.com/user-attachments/assets/ea4f60c2-1abb-42b7-932f-a40cfb037a1b" />
<img width="1118" height="354" alt="7e9556ef5012852a22e36703cfc7e2a4" src="https://github.com/user-attachments/assets/7e3c27f2-cdf1-433b-aa8d-185dda1c931b" />
<img width="1008" height="515" alt="8b1d18e4657c15b8ef1472f6efb5a8de" src="https://github.com/user-attachments/assets/a08920ea-fb85-4a8b-8d5b-acddf4a2f63e" />
<img width="1093" height="461" alt="d23b76e1bee0234a85345ddc51742f0f" src="https://github.com/user-attachments/assets/6ca5f574-d625-4585-838c-2faca798c815" />
<img width="1090" height="415" alt="efedd23ceda39d885d489391eb8940f9" src="https://github.com/user-attachments/assets/3d6c8065-9810-47d6-b909-3c0c0cba2005" />







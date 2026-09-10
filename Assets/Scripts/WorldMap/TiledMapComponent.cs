/*
 * 文件名: WorldObjectManager.cs
 * 作者: zengxin
 * 创建日期: 2026-7-2
 */
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
namespace Scx { 
    
    public class TiledMapComponent : MonoBehaviour 
    {
        public Material materialDefaultFile;
 
        private Dictionary<string,SpritesContainer> spritesCache = new ();
        
        public X_UniTMX.Map TiledMap { get; private set; }
        
        public async UniTask LoadMap(string mapPath)
        {
            ResourceRequest request = Resources.LoadAsync<TextAsset>(mapPath);
            await request.ToUniTask();
            TextAsset textAsset = request.asset as TextAsset;
            if (TiledMap != null)
            {
                Destroy(TiledMap.MapObject);
            }
            TiledMap = new X_UniTMX.Map(textAsset, mapPath, gameObject, materialDefaultFile, 0, LoaderTexture, LoaderSprite);
            await TiledMap.WhenLoaded();

            // 赋值而非累乘：重复加载（换图）时 localScale 不会指数级增长（254 → 254² → ...）
            transform.localScale = Vector3.one * TiledMap.TileWidth;
            // 本地坐标：不依赖父级变换（世界坐标 position 会忽略父级的位置/缩放）
            transform.localPosition = new Vector3(0, 0, 100);
        }

        private async UniTask< Texture2D> LoaderTexture(string path)
        {
            path = path.Replace("\\", "/");
            ResourceRequest request = Resources.LoadAsync<Texture2D>(path);
            await request.ToUniTask();
            return request.asset as Texture2D;
        }

        private async UniTask<Sprite> LoaderSprite(string path)
        {
            string prefabPath = System.IO.Path.GetDirectoryName(path);
            prefabPath = prefabPath?.Replace("\\", "/");
            string spriteName = System.IO.Path.GetFileName(path);
            if (!spritesCache.TryGetValue(prefabPath, out SpritesContainer container))
            {
                ResourceRequest request = Resources.LoadAsync<GameObject>(prefabPath);
                await request.ToUniTask();
                GameObject containerObj = request.asset as GameObject;
                container = containerObj.GetComponent<SpritesContainer>();
                spritesCache[prefabPath] = container;
                container.Setup();
            }
            return container.Setup().GetSpriteByName(spriteName);
        }
    }
}
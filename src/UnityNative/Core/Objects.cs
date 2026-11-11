// 場景物件的原生資料：GameObject、元件、Transform／RectTransform、場景。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityNative
{
    public class NScene
    {
        public int Handle;
        public string Name = "";
        public string Path = "";
        public string Guid = "";
        public int BuildIndex = -1;
        public bool IsLoaded;
        /// <summary>載入時的 fileID 表（SceneRefs 用）</summary>
        public LoadContext LoadCtx;
        public UnityEngine.SceneManagement.Scene.LoadingState LoadingState;
        public readonly List<NGameObject> Roots = new List<NGameObject>();
        public UnityEngine.SceneManagement.Scene ToScene() => new UnityEngine.SceneManagement.Scene { m_Handle = Handle };
    }

    public class NGameObject : NObject
    {
        public readonly List<NComponent> Components = new List<NComponent>();
        public NTransform Transform;
        public int Layer;
        public string Tag = "Untagged";
        public bool ActiveSelf = true;
        /// <summary>照父物件一路算下來的結果，SetActive、SetParent 時重算整棵子樹</summary>
        public bool ActiveInHierarchy;
        public bool IsStatic;
        public NScene Scene;

        public NGameObject Parent => Transform?.Parent?.Go;

        public GameObject GO => (GameObject)Managed;

        public T Get<T>() where T : NComponent
        {
            foreach (var c in Components) if (c is T t && c.Alive) return t;
            return null;
        }
    }

    public class NComponent : NObject
    {
        public NGameObject Go;
        /// <summary>場景或 prefab 檔裡的序列化資料（原生元件用它補欄位，例如 Camera 的 orthographic size）。只在載入時有，Loader 填完欄位就清掉</summary>
        public System.Text.Json.Nodes.JsonObject Data;
        /// <summary>同一份資料的 JsonElement（Loader 載入時才有，跟 Data 一起清掉）：讀很多欄位的元件（粒子系統）直接讀它，不展開 JsonNode</summary>
        public System.Text.Json.JsonElement El;
        public NTransform Tr => Go?.Transform;

        /// <summary>從場景、prefab 的 JSON 讀原生欄位（Data 已經設好；參照到別的物件的欄位要等這時才找得到）</summary>
        public virtual void Load(System.Text.Json.Nodes.JsonObject data, LoadContext ctx) { }
        /// <summary>Instantiate 時複製原生那半邊的狀態（managed 欄位由 Cloner 處理）</summary>
        public virtual void CopyFrom(NComponent src) { Data = src.Data; }
        /// <summary>GameObject 在階層裡啟用、停用時（原生元件用，例如把 Canvas 加進繪製清單）</summary>
        public virtual void OnActiveChanged() { }
        public virtual void OnDestroyNative() { }
    }

    public class NBehaviour : NComponent
    {
        public bool Enabled = true;
        public override void CopyFrom(NComponent src) { base.CopyFrom(src); Enabled = ((NBehaviour)src).Enabled; }
        public virtual bool IsActiveAndEnabled => Enabled && Go != null && Go.ActiveInHierarchy;
    }

    /// <summary>沒有特別實作的原生元件（Light、AudioListener…）：讓 GetComponent 拿得到</summary>
    public class NGenericComponent : NBehaviour { }
}

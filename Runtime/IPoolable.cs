using UnityEngine;
using com.fscigliano.CommonExtensions;

namespace com.fscigliano.PoolingSystem
{
    public interface IPoolable
    {
        GameObject gameObject { get; }
        Transform transform { get; }
        void OnSpawn();
        void OnReturn();
        void SetPoolManager(PoolManager poolManager, IDAsset poolID);
    }
}
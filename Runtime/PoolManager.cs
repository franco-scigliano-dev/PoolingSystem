using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using com.fscigliano.CommonExtensions;

namespace com.fscigliano.PoolingSystem
{
    public class PoolManager : MonoBehaviour
    {
        [System.Serializable]
        public class PrefabEntry
        { 
            public IDAsset poolID;
            public AssetReferenceGameObject prefabReference;
            public int preloadCount = 5;
            public int defaultCapacity = 10;
            public int maxSize = 100;
        }

        [SerializeField] protected List<PrefabEntry> _prefabEntries;
        [SerializeField] protected bool _initializeOnAwake = true;
        
        protected Dictionary<IDAsset, ObjectPool<IPoolable>> _pools;
        protected Dictionary<IDAsset, Transform> _poolContainers;
        protected Dictionary<IDAsset, GameObject> _loadedPrefabs;
        protected Dictionary<IDAsset, AsyncOperationHandle<GameObject>> _loadingHandles;
        
        public bool IsInitialized { get; protected set; }
        public bool IsInitializing { get; protected set; }

        protected virtual void Awake()
        {
            if (_initializeOnAwake)
            {
                _ = InitializePoolsAsync();
            }
        }

        public virtual async Task InitializePoolsAsync()
        {
            if (IsInitializing || IsInitialized)
            {
                Debug.LogWarning($"[PoolManager:{name}] Already initialized or initializing!");
                return;
            }

            IsInitializing = true;
            Debug.Log($"[PoolManager:{name}] Starting addressable prefab loading...");

            _pools = new Dictionary<IDAsset, ObjectPool<IPoolable>>();
            _poolContainers = new Dictionary<IDAsset, Transform>();
            _loadedPrefabs = new Dictionary<IDAsset, GameObject>();
            _loadingHandles = new Dictionary<IDAsset, AsyncOperationHandle<GameObject>>();

            // Load all prefabs first
            var loadTasks = new List<System.Threading.Tasks.Task>();
            
            foreach (var entry in _prefabEntries)
            {
                if (entry.poolID == null)
                {
                    Debug.LogError($"[PoolManager:{name}] Pool ID is null for entry!");
                    continue;
                }

                if (!entry.prefabReference.RuntimeKeyIsValid())
                {
                    Debug.LogError($"[PoolManager:{name}] Invalid prefab reference for pool: {entry.poolID.name}");
                    continue;
                }

                loadTasks.Add(LoadPrefabAsync(entry));
            }

            // Wait for all prefabs to load
            await System.Threading.Tasks.Task.WhenAll(loadTasks);

            // Create pools for successfully loaded prefabs
            foreach (var entry in _prefabEntries)
            {
                if (entry.poolID == null || !_loadedPrefabs.ContainsKey(entry.poolID))
                    continue;

                CreatePool(entry);
            }

            IsInitializing = false;
            IsInitialized = true;
            Debug.Log($"[PoolManager:{name}] Initialization complete! Created {_pools.Count} pools.");
        }

        protected async Task LoadPrefabAsync(PrefabEntry entry)
        {
            try
            {
                Debug.Log($"[PoolManager:{name}] Loading prefab for pool: {entry.poolID.name}");
                
                var handle = Addressables.LoadAssetAsync<GameObject>(entry.prefabReference);
                _loadingHandles[entry.poolID] = handle;
                
                var prefabGameObject = await handle.Task;
                
                if (prefabGameObject == null)
                {
                    Debug.LogError($"[PoolManager:{name}] Failed to load prefab for pool: {entry.poolID.name}");
                    return;
                }

                var poolableComponent = prefabGameObject.GetComponent<IPoolable>();
                if (poolableComponent == null)
                {
                    Debug.LogError($"[PoolManager:{name}] Loaded prefab does not implement IPoolable for pool: {entry.poolID.name}");
                    Addressables.Release(handle);
                    return;
                }

                _loadedPrefabs[entry.poolID] = prefabGameObject;
                Debug.Log($"[PoolManager:{name}] Successfully loaded prefab for pool: {entry.poolID.name}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PoolManager:{name}] Exception loading prefab for pool {entry.poolID.name}: {ex.Message}");
            }
        }

        protected void CreatePool(PrefabEntry entry)
        {
            if (!_loadedPrefabs.TryGetValue(entry.poolID, out var prefabGameObject))
                return;

            // Create container for this pool
            var container = CreatePoolContainer(entry.poolID);
            _poolContainers[entry.poolID] = container;

            var pool = new ObjectPool<IPoolable>(
                createFunc: () => CreatePoolableObject(entry.poolID, prefabGameObject, container),
                actionOnGet: obj => 
                {
                    // Don't enable here - position must be set first
                    obj.OnSpawn();
                },
                actionOnRelease: obj => 
                {
                    obj.OnReturn();
                    obj.gameObject.SetActive(false);
                },
                actionOnDestroy: obj => Destroy(obj.gameObject),
                collectionCheck: false,
                defaultCapacity: entry.defaultCapacity,
                maxSize: entry.maxSize
            );

            _pools[entry.poolID] = pool;

            // Preload objects
            PreloadObjects(pool, entry.preloadCount);
            
            Debug.Log($"[PoolManager:{name}] Created pool '{entry.poolID.name}' with {entry.preloadCount} preloaded objects");
        }

        protected Transform CreatePoolContainer(IDAsset poolID)
        {
            string containerName = $"Pool_{poolID.name}";
            GameObject container = new GameObject(containerName);
            container.transform.SetParent(transform);
            return container.transform;
        }

        protected IPoolable CreatePoolableObject(IDAsset poolID, GameObject prefabGameObject, Transform container)
        {
            var instance = Instantiate(prefabGameObject, container);
            var poolable = instance.GetComponent<IPoolable>();
            
            if (poolable == null)
            {
                Debug.LogError($"[PoolManager:{name}] Instantiated object {instance.name} does not have IPoolable component!");
                Destroy(instance);
                return null;
            }
            
            // Set references so the object can return itself to the correct pool
            poolable.SetPoolManager(this, poolID);
            
            instance.SetActive(false);
            return poolable;
        }

        protected void PreloadObjects(ObjectPool<IPoolable> pool, int count)
        {
            var preloadedObjects = new List<IPoolable>();
            
            for (int i = 0; i < count; i++)
            {
                var obj = pool.Get();
                preloadedObjects.Add(obj);
            }
            
            foreach (var obj in preloadedObjects)
            {
                pool.Release(obj);
            }
        }

        public IPoolable Spawn(IDAsset poolID, Vector3 position, Quaternion rotation)
        {
            if (!IsInitialized)
            {
                Debug.LogWarning($"[PoolManager:{name}] Cannot spawn - PoolManager not initialized yet!");
                return null;
            }

            if (!_pools.ContainsKey(poolID))
            {
                Debug.LogWarning($"[PoolManager:{name}] No pool configured for ID: {poolID.name}");
                return null;
            }

            var obj = _pools[poolID].Get();
            
            // Set position and rotation BEFORE enabling
            obj.transform.SetPositionAndRotation(position, rotation);
            
            // Now it's safe to enable the object
            obj.gameObject.SetActive(true);
            
            return obj;
        }

        public IPoolable Spawn(IDAsset poolID)
        {
            return Spawn(poolID, Vector3.zero, Quaternion.identity);
        }

        public void Return(IDAsset poolID, IPoolable poolable)
        {
            if (_pools.ContainsKey(poolID))
            {
                _pools[poolID].Release(poolable);
            }
            else
            {
                Debug.LogWarning($"[PoolManager:{name}] No pool found for ID: {poolID.name}");
            }
        }

        public bool HasPool(IDAsset poolID)
        {
            return _pools.ContainsKey(poolID);
        }

        public GameObject GetLoadedPrefab(IDAsset poolID)
        {
            return _loadedPrefabs.TryGetValue(poolID, out var prefab) ? prefab : null;
        }

        private void OnDestroy()
        {
            // Release all addressable handles
            foreach (var handle in _loadingHandles.Values)
            {
                if (handle.IsValid())
                {
                    Addressables.Release(handle);
                }
            }
        }
    }
}

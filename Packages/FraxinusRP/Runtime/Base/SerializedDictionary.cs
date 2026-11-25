using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FraxinusEngine.Base
{
    /// <summary>
    /// 可序列化的字典（用于 Unity Inspector）
    /// </summary>
    [Serializable]
    public class SerializedDictionary<TKey, TValue> : ISerializationCallbackReceiver, IEnumerable<KeyValuePair<TKey, TValue>>
    {
        [SerializeField] private List<TKey> _keys = new List<TKey>();
        [SerializeField] private List<TValue> _values = new List<TValue>();

        private Dictionary<TKey, TValue> _dictionary = new Dictionary<TKey, TValue>();

        /// <summary>
        /// 获取或设置指定键的值
        /// </summary>
        public TValue this[TKey key]
        {
            get => _dictionary[key];
            set => _dictionary[key] = value;
        }

        /// <summary>
        /// 尝试获取指定键的值
        /// </summary>
        public bool TryGetValue(TKey key, out TValue value)
        {
            return _dictionary.TryGetValue(key, out value);
        }

        /// <summary>
        /// 检查是否包含指定键
        /// </summary>
        public bool ContainsKey(TKey key)
        {
            return _dictionary.ContainsKey(key);
        }

        /// <summary>
        /// 添加键值对
        /// </summary>
        public void Add(TKey key, TValue value)
        {
            _dictionary.Add(key, value);
        }

        /// <summary>
        /// 移除指定键
        /// </summary>
        public bool Remove(TKey key)
        {
            return _dictionary.Remove(key);
        }

        /// <summary>
        /// 清空字典
        /// </summary>
        public void Clear()
        {
            _dictionary.Clear();
        }

        /// <summary>
        /// 获取键集合
        /// </summary>
        public Dictionary<TKey, TValue>.KeyCollection Keys => _dictionary.Keys;

        /// <summary>
        /// 获取值集合
        /// </summary>
        public Dictionary<TKey, TValue>.ValueCollection Values => _dictionary.Values;

        /// <summary>
        /// 获取键值对数量
        /// </summary>
        public int Count => _dictionary.Count;

        /// <summary>
        /// 获取枚举器
        /// </summary>
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
        {
            return _dictionary.GetEnumerator();
        }

        /// <summary>
        /// 获取枚举器（非泛型）
        /// </summary>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        /// 序列化前回调
        /// </summary>
        public void OnBeforeSerialize()
        {
            _keys.Clear();
            _values.Clear();

            foreach (var kvp in _dictionary)
            {
                _keys.Add(kvp.Key);
                _values.Add(kvp.Value);
            }
        }

        /// <summary>
        /// 反序列化后回调
        /// </summary>
        public void OnAfterDeserialize()
        {
            _dictionary.Clear();

            if (_keys.Count != _values.Count)
            {
                Debug.LogError($"SerializedDictionary 反序列化失败：键和值的数量不匹配（{_keys.Count} vs {_values.Count}）");
                return;
            }

            for (int i = 0; i < _keys.Count; i++)
            {
                if (_keys[i] != null && !_dictionary.ContainsKey(_keys[i]))
                {
                    _dictionary.Add(_keys[i], _values[i]);
                }
            }
        }
    }
}

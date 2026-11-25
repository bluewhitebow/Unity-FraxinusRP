using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FraxinusEngine.Rendering.Ultra
{
    /// <summary>
    /// 上下文状态管理器
    /// 管理作用域切换和状态栈
    /// </summary>
    public class ContextStateManager : ContextItem
    {
        /// <summary>主作用域的 contextId（与 <see cref="AllocateSubContextId"/> 生成的子 ID 区分）</summary>
        public const int mainContextId = 0;

        private readonly Stack<ContextInfo> _contextStack = new(4);
        private readonly ContextData _mainData = new();
        private readonly HashSet<(InjectPass pass, int contextId)> _executedInjectors = new();
        private readonly List<int> _executedFallbackCounts = new();
        private ContextContainer _frameData;

        public int depth => _contextStack.Count;

        public void EnsureMainContext(ContextContainer frameData)
        {
            if (_currentContext.data != null)
                return;
            _frameData = frameData;
            _mainData.Set(frameData.Get<ResourceData>());
            _mainData.Set(frameData.Get<CameraData>());
            _mainData.Set(frameData.Get<RenderingData>());
            SetCurrentContext(new ContextInfo { scope = ContextScope.Main, data = _mainData, contextName = "Main" });
        }

        private void SetCurrentContext(ContextInfo context)
        {
            _currentContext = context;
            // The normal main bindings alias owned frame data, so skip that lookup.
            // Custom contexts can override any registered type, including in main scope.
            _frameData?.SetCurrentScope(ReferenceEquals(context.data, _mainData) ? null : context.data);
        }

        public bool AllowsInjection(ContextScopeFilter filter)
        {
            if (IsInSubContext() && _currentContext.injectPassMode == InjectPassFilterMode.Disabled)
                return false;
            var flag = IsInSubContext() ? ContextScopeFilter.Sub : ContextScopeFilter.Main;
            return (filter & flag) != 0;
        }

        internal bool TryBeginInjection(InjectPass pass, ContextScopeFilter filter)
        {
            if (!AllowsInjection(filter) || !_executedInjectors.Add((pass, _currentContext.contextId)))
                return false;

            if (AllowsInjection(pass.fallbackScopeFilter))
            {
                while (_executedFallbackCounts.Count <= _currentContext.contextId)
                    _executedFallbackCounts.Add(0);
                ++_executedFallbackCounts[_currentContext.contextId];
            }
            return true;
        }

        internal bool HasPendingFallbacks(int count)
        {
            if (count == 0 || !AllowsInjection(ContextScopeFilter.All))
                return false;
            int contextId = _currentContext.contextId;
            return contextId >= _executedFallbackCounts.Count || _executedFallbackCounts[contextId] < count;
        }
        private ContextInfo _currentContext;
        private Dictionary<int, ContextInfo> _subContextRegistry = new();
        private int _nextSubContextId = 1;
        
        public void PushContext(ContextInfo context)
        {
            _contextStack.Push(context);
            SetCurrentContext(context);
        }
        
        public ContextInfo PopContext()
        {
            if (_contextStack.Count == 0)
            {
                Debug.LogError("ContextStateManager: ContextStack is empty!");
                return default;
            }
            
            var context = _contextStack.Pop();
            SetCurrentContext(context);
            return context;
        }
        
        /// <summary>为子作用域分配唯一 ID（无堆分配，区别于 Guid.ToString）。</summary>
        public int AllocateSubContextId()
        {
            return _nextSubContextId++;
        }

        public void EnterSubContext(ContextInfo subContext)
        {
            SetCurrentContext(subContext);
            _subContextRegistry[subContext.contextId] = subContext;
        }
        
        public void ExitSubContext()
        {
            if (_contextStack.Count > 0)
                SetCurrentContext(_contextStack.Peek());
        }
        
        public ContextInfo GetCurrentContext()
        {
            return _currentContext;
        }
        
        public ContextInfo GetSubContext(int contextId)
        {
            return _subContextRegistry.TryGetValue(contextId, out var context) ? context : default;
        }
        
        public bool IsInMainContext()
        {
            return _currentContext.scope == ContextScope.Main;
        }
        
        public bool IsInSubContext()
        {
            return _currentContext.scope == ContextScope.Sub;
        }
        
        /// <summary>获取当前作用域的数据引用；未设置时返回 null。</summary>
        public T Get<T>() where T : ContextItem
        {
            return _currentContext.data?.Get<T>();
        }
        
        public override void Reset()
        {
            _frameData?.SetCurrentScope(null);
            _frameData = null;
            _contextStack.Clear();
            _executedInjectors.Clear();
            _executedFallbackCounts.Clear();
            _mainData.Clear();
            _subContextRegistry.Clear();
            _currentContext = default;
            _nextSubContextId = 1;
        }
    }
    
    /// <summary>
    /// 上下文信息结构
    /// 存储作用域的完整数据
    /// </summary>
    public struct ContextInfo
    {
        /// <summary>作用域类型</summary>
        public ContextScope scope;

        public InjectPassFilterMode injectPassMode;

        internal ContextTextureBindings textureBindings;
        
        /// <summary>作用域唯一标识（主作用域为 <see cref="ContextStateManager.mainContextId"/>）</summary>
        public int contextId;
        
        /// <summary>作用域可读名称</summary>
        public string contextName;
        
        /// <summary>当前作用域借用的数据引用。</summary>
        public ContextData data;
        
        /// <summary>开始时间（用于性能统计）</summary>
        public float startTime;
    }
}

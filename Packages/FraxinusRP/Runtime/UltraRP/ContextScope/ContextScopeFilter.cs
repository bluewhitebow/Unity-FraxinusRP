using System;

namespace FraxinusEngine.Rendering.Ultra
{
    [Flags]
    public enum ContextScopeFilter
    {
        None = 0,
        
        Main = 1 << 0,
        
        Sub = 1 << 1,
        
        All = Main | Sub
    }
    
    public enum ContextScope
    {
        Main,
        
        Sub
    }
}

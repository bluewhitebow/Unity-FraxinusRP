using Object = UnityEngine.Object;

namespace FraxinusEngine.Base
{
    public interface ICreateNodeFrom<T> where T : Object
    {
        bool InitializeNodeFromObject(T value);
    }
}
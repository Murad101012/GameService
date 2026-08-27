namespace Interfaces
{
    public interface ILevelRegistry
    {
        void Register<T>(T entity);
        void Unregister<T>(T entity);
    }
}
﻿namespace Elders.Cronus.Projections.Cassandra.EventSourcing
{
    public interface IProjectionGetResult<out T>
    {
        bool Success { get; }

        T Projection { get; }
    }

    public class ProjectionGetResult<T> : IProjectionGetResult<T>
    {
        public ProjectionGetResult(bool success, T projection)
        {
            Success = success;
            Projection = projection;
        }

        public bool Success { get; private set; }

        public T Projection { get; private set; }

        public static IProjectionGetResult<T> NoResult = new ProjectionGetResult<T>(false, default(T));
    }
}

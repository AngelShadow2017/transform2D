using System;

namespace Core.TrueSync.Collision
{
    public interface ICollisionProvider
    {
        void Add(TSVector2 position, uint entityId);
        void Clear();
        void ForEachBlock(Action<BlockBox> action);
        FP BlockSize { get; }

        bool CollisionDetection(TSVector2 position,Func<uint,bool> detectionRange);
    }
}

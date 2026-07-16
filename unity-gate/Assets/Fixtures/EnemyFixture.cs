using UnityEngine;

namespace MyGame.Enemies
{
    // A user MonoBehaviour referenced by its SHORT name (`Component<Enemy>` under
    // `using MyGame.Enemies;`) — exercises the unqualified-type-name resolution/identity path
    // (specs/20-unqualified-type-names.md).
    public class Enemy : MonoBehaviour
    {
        public int health;
    }
}

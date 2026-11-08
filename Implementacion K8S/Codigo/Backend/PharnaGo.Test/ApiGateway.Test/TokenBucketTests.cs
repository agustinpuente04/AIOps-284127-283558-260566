using PharmaGo.ApiGateway.RateLimiting;

namespace PharmaGo.Test.ApiGateway.Test
{
    [TestClass]
    public class TokenBucketTests
    {
        // 60 tokens/min = 1 token por segundo: facilita las cuentas
        private static TokenBucket Bucket(double capacity = 5, double perMinute = 60, long now = 0) => new(capacity, perMinute, now);

        [TestMethod]
        public void TryConsume_AllowsBurstUpToCapacity_ThenDenies()
        {
            var bucket = Bucket(capacity: 5);
            for (var i = 0; i < 5; i++) Assert.IsTrue(bucket.TryConsume(0, out _), $"request {i + 1} dentro de la ráfaga");
            Assert.IsFalse(bucket.TryConsume(0, out _));
        }

        [TestMethod]
        public void TryConsume_Denied_ReportsHowLongToWait()
        {
            var bucket = Bucket(capacity: 1);
            Assert.IsTrue(bucket.TryConsume(0, out _));
            Assert.IsFalse(bucket.TryConsume(0, out var retryAfter));
            Assert.AreEqual(1000, retryAfter.TotalMilliseconds, 1);
        }

        [TestMethod]
        public void TryConsume_RecoversGradually_NotAllAtOnce()
        {
            var bucket = Bucket(capacity: 5);
            for (var i = 0; i < 5; i++) bucket.TryConsume(0, out _);

            // tras 2 s hay exactamente 2 tokens: la recuperación es gradual, no hay que esperar a "la próxima hora"
            Assert.IsTrue(bucket.TryConsume(2000, out _));
            Assert.IsTrue(bucket.TryConsume(2000, out _));
            Assert.IsFalse(bucket.TryConsume(2000, out _));
        }

        [TestMethod]
        public void TryConsume_NeverExceedsCapacity_AfterLongIdle()
        {
            var bucket = Bucket(capacity: 3);
            bucket.TryConsume(0, out _);
            var allowed = 0;
            while (bucket.TryConsume(3_600_000, out _) && allowed < 100) allowed++;
            Assert.AreEqual(3, allowed, "una hora de inactividad no acumula más que la capacidad");
        }

        [TestMethod]
        public void SustainedRate_IsLimitedToRefillRate()
        {
            // Lo que la ventana fija no garantizaba: a 1 req/ms durante 60 s solo pasan capacidad + recarga
            var bucket = Bucket(capacity: 10, perMinute: 60);
            var allowed = 0;
            for (long t = 0; t <= 60_000; t++) if (bucket.TryConsume(t, out _)) allowed++;
            Assert.IsTrue(allowed >= 69 && allowed <= 71, $"permitidas: {allowed}");
        }

        [TestMethod]
        public void HasTokens_DoesNotConsume()
        {
            var bucket = Bucket(capacity: 1);
            Assert.IsTrue(bucket.HasTokens(0, out _));
            Assert.IsTrue(bucket.HasTokens(0, out _));
            Assert.IsTrue(bucket.TryConsume(0, out _));
            Assert.IsFalse(bucket.HasTokens(0, out _));
        }

        [TestMethod]
        public void IsFull_OnlyAfterRefillingCompletely()
        {
            var bucket = Bucket(capacity: 5);
            Assert.IsTrue(bucket.IsFull(0));
            bucket.TryConsume(0, out _);
            Assert.IsFalse(bucket.IsFull(0));
            Assert.IsTrue(bucket.IsFull(1000));
        }
    }

    [TestClass]
    public class BucketStoreTests
    {
        private static readonly BucketOptions Opts = new() { Capacity = 3, RefillPerMinute = 60 };

        [TestMethod]
        public void SameKey_SharesBucket_DifferentKeysAreIndependent()
        {
            var store = new BucketStore(100);
            for (var i = 0; i < 3; i++) store.GetOrCreate("ip:1.1.1.1", Opts, 0).TryConsume(0, out _);

            Assert.IsFalse(store.GetOrCreate("ip:1.1.1.1", Opts, 0).TryConsume(0, out _), "la IP agotó su cubo");
            Assert.IsTrue(store.GetOrCreate("ip:2.2.2.2", Opts, 0).TryConsume(0, out _), "otra IP no se ve afectada");
            Assert.IsTrue(store.GetOrCreate("u:7", Opts, 0).TryConsume(0, out _), "un usuario tiene su propio cubo");
        }

        [TestMethod]
        public void Sweep_RemovesOnlyFullBuckets_WithoutChangingAnyLimit()
        {
            var store = new BucketStore(100);
            store.GetOrCreate("ip:idle", Opts, 0);
            var busy = store.GetOrCreate("ip:busy", Opts, 0);
            for (var i = 0; i < 3; i++) busy.TryConsume(0, out _);

            Assert.AreEqual(1, store.Sweep(0), "solo el cubo lleno se descarta");
            Assert.AreEqual(1, store.Count);
            Assert.IsFalse(store.GetOrCreate("ip:busy", Opts, 0).TryConsume(0, out _), "el cubo vacío sigue limitando");
        }

        [TestMethod]
        public void KeyCap_BoundsMemory_NewKeysShareAnOverflowBucket()
        {
            var store = new BucketStore(maxKeys: 2);
            store.GetOrCreate("ip:a", Opts, 0);
            store.GetOrCreate("ip:b", Opts, 0);

            // un atacante que inventa IPs no puede crear claves sin límite: comparten un cubo estricto
            for (var i = 0; i < 50; i++) store.GetOrCreate($"ip:spoofed-{i}", Opts, 0).TryConsume(0, out _);

            Assert.IsTrue(store.Count <= 3, $"claves en memoria: {store.Count}");
            Assert.IsFalse(store.GetOrCreate("ip:spoofed-999", Opts, 0).TryConsume(0, out _), "el cubo overflow ya está agotado");
        }
    }
}

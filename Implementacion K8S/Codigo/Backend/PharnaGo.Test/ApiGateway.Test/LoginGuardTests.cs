using PharmaGo.ApiGateway.RateLimiting;

namespace PharmaGo.Test.ApiGateway.Test
{
    [TestClass]
    public class LoginGuardTests
    {
        private const long Sec = 1000;
        private static LoginOptions Options() => new() { BaseLockSeconds = 30, MaxLockSeconds = 900, FailureWindowSeconds = 900, KnownGoodHours = 24 };
        private static LoginGuard Guard() => new(Options(), 1000);

        [TestMethod]
        public void BelowThreshold_NoLock()
        {
            var guard = Guard();
            for (var i = 0; i < 4; i++) Assert.AreEqual(TimeSpan.Zero, guard.RecordFailure("ana|1.1.1.1", 5, i * Sec));
            Assert.IsFalse(guard.IsLocked("ana|1.1.1.1", 5 * Sec, out _));
        }

        [TestMethod]
        public void ReachingThreshold_LocksForBaseDuration()
        {
            var guard = Guard();
            TimeSpan lockFor = TimeSpan.Zero;
            for (var i = 0; i < 5; i++) lockFor = guard.RecordFailure("ana|1.1.1.1", 5, i * Sec);

            Assert.AreEqual(30, lockFor.TotalSeconds);
            Assert.IsTrue(guard.IsLocked("ana|1.1.1.1", 5 * Sec, out var remaining));
            Assert.IsTrue(remaining.TotalSeconds is > 25 and <= 30);
        }

        [TestMethod]
        public void LockExpires_AndEachNewFailureDoublesIt_UpToTheMaximum()
        {
            var guard = Guard();
            long t = 0;
            for (var i = 0; i < 5; i++) guard.RecordFailure("ana|ip", 5, t += Sec);   // primer bloqueo: 30 s

            var expected = new[] { 60, 120, 240, 480, 900, 900, 900 };   // se duplica y se topa en 900
            var previous = 30;
            foreach (var seconds in expected)
            {
                t += (previous + 1) * Sec;                           // vuelve a intentar apenas vence el bloqueo anterior
                Assert.IsFalse(guard.IsLocked("ana|ip", t, out _));
                Assert.AreEqual(seconds, guard.RecordFailure("ana|ip", 5, t).TotalSeconds, $"bloqueo esperado {seconds}s");
                previous = seconds;
            }
        }

        [TestMethod]
        public void Hammering_DuringALock_IsRejectedByCallerAndDoesNotExtendIt()
        {
            // El middleware no registra intentos mientras IsLocked es true: el estado no cambia
            var guard = Guard();
            for (var i = 0; i < 5; i++) guard.RecordFailure("ana|ip", 5, i * Sec);
            guard.IsLocked("ana|ip", 6 * Sec, out var first);
            for (var i = 0; i < 100; i++) guard.IsLocked("ana|ip", 6 * Sec, out _);
            guard.IsLocked("ana|ip", 6 * Sec, out var after);
            Assert.AreEqual(first, after);
        }

        [TestMethod]
        public void MaxLock_DoesNotEraseTheHistory_WhenItExpires()
        {
            // Regresión: con bloqueo máximo == ventana de fallos, el historial no puede borrarse justo al vencer el bloqueo
            var guard = Guard();
            long t = 0;
            double lockSeconds = 0;
            for (var i = 0; i < 5; i++) lockSeconds = guard.RecordFailure("ana|ip", 5, t += Sec).TotalSeconds;   // 30 s
            while (lockSeconds < 900)                                  // el atacante reintenta apenas vence cada bloqueo
            {
                t += (long)(lockSeconds + 1) * Sec;
                lockSeconds = guard.RecordFailure("ana|ip", 5, t).TotalSeconds;
            }
            t += 901 * Sec;                                            // vence el bloqueo máximo (900 s == ventana de fallos)
            Assert.AreEqual(900, guard.RecordFailure("ana|ip", 5, t).TotalSeconds, "sigue en el bloqueo máximo, no vuelve a empezar");
        }

        [TestMethod]
        public void FailuresOlderThanTheWindow_StartOver()
        {
            var guard = Guard();
            for (var i = 0; i < 4; i++) guard.RecordFailure("ana|ip", 5, i * Sec);
            // pasada la ventana (15 min) el contador vuelve a cero: este fallo es el primero, no el quinto
            Assert.AreEqual(TimeSpan.Zero, guard.RecordFailure("ana|ip", 5, 20 * 60 * Sec));
        }

        [TestMethod]
        public void Success_ClearsFailures_AndMarksTheDeviceAsKnown()
        {
            var guard = Guard();
            for (var i = 0; i < 4; i++) guard.RecordFailure("ana|ip", 5, i * Sec);
            guard.RecordSuccess("ana|ip", 10 * Sec);

            Assert.AreEqual(TimeSpan.Zero, guard.RecordFailure("ana|ip", 5, 11 * Sec), "tras un éxito se vuelve a empezar");
            Assert.IsTrue(guard.IsKnownGood("ana|ip", 12 * Sec));
            Assert.IsFalse(guard.IsKnownGood("ana|otra-ip", 12 * Sec));
            Assert.IsFalse(guard.IsKnownGood("ana|ip", 25L * 3600 * Sec), "el dispositivo conocido caduca");
        }

        [TestMethod]
        public void AccountKeys_AreIndependent_AttackerCannotLockTheOwnerFromAnotherIp()
        {
            var guard = Guard();
            for (var i = 0; i < 5; i++) guard.RecordFailure("ana|9.9.9.9", 5, i * Sec);   // el atacante

            Assert.IsTrue(guard.IsLocked("ana|9.9.9.9", 6 * Sec, out _));
            Assert.IsFalse(guard.IsLocked("ana|1.1.1.1", 6 * Sec, out _), "la dueña desde su IP no está bloqueada");
        }

        [TestMethod]
        public void GlobalAccountKey_LocksDistributedAttack_WithHigherThreshold()
        {
            var guard = Guard();
            // 19 fallos desde IPs distintas: aún no hay bloqueo global; el 20.º sí
            for (var i = 0; i < 19; i++) Assert.AreEqual(TimeSpan.Zero, guard.RecordFailure("ana", 20, i * Sec));
            Assert.IsTrue(guard.RecordFailure("ana", 20, 19 * Sec) > TimeSpan.Zero);
            Assert.IsTrue(guard.IsLocked("ana", 20 * Sec, out _));
        }

        [TestMethod]
        public void KeyCap_BoundsMemory()
        {
            var guard = new LoginGuard(Options(), maxKeys: 10);
            for (var i = 0; i < 1000; i++) guard.RecordFailure($"user{i}|ip", 5, 0);
            Assert.IsTrue(guard.Count <= 10, $"estados en memoria: {guard.Count}");
        }

        [TestMethod]
        public void Sweep_DropsStaleStates_KeepsActiveLocks()
        {
            var guard = Guard();
            guard.RecordFailure("viejo|ip", 5, 0);
            for (var i = 0; i < 5; i++) guard.RecordFailure("bloqueado|ip", 5, 3_000_000 + i * Sec);

            guard.Sweep(3_005 * Sec);

            Assert.IsFalse(guard.IsLocked("viejo|ip", 3_005 * Sec, out _));
            Assert.IsTrue(guard.IsLocked("bloqueado|ip", 3_005 * Sec, out _), "un bloqueo vigente no se descarta");
        }
    }
}

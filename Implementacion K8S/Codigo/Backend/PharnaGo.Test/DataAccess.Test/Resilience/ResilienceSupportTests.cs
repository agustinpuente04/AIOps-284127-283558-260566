using PharmaGo.DataAccess.Resilience;

namespace PharmaGo.Test.DataAccess.Test.Resilience
{
    [TestClass]
    public class EqualJitterTests
    {
        private static readonly TimeSpan Base = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan Max = TimeSpan.FromMilliseconds(2000);

        [TestMethod]
        public void HalfTheDelayIsFixed_TheOtherHalfIsRandom()
        {
            // intento 1: techo 200 ms -> [100, 200]
            Assert.AreEqual(100, EqualJitter.Delay(1, Base, Max, () => 0.0).TotalMilliseconds, 0.001);
            Assert.AreEqual(150, EqualJitter.Delay(1, Base, Max, () => 0.5).TotalMilliseconds, 0.001);
            Assert.AreEqual(200, EqualJitter.Delay(1, Base, Max, () => 1.0).TotalMilliseconds, 0.001);
        }

        [TestMethod]
        public void NeverAwaitsLessThanHalfTheCeiling_UnlikeFullJitter()
        {
            // El jitter completo puede dar ~0 ms y reintentar al instante; el equal jitter garantiza una espera mínima
            for (var attempt = 1; attempt <= 8; attempt++)
            {
                var ceiling = Math.Min(2000, 200 * Math.Pow(2, attempt - 1));
                Assert.IsTrue(EqualJitter.Delay(attempt, Base, Max, () => 0.0).TotalMilliseconds >= ceiling / 2 - 0.001, $"intento {attempt}");
            }
        }

        [TestMethod]
        public void GrowsExponentially_UntilTheCap()
        {
            var minimums = Enumerable.Range(1, 6).Select(a => EqualJitter.Delay(a, Base, Max, () => 0.0).TotalMilliseconds).ToArray();
            CollectionAssert.AreEqual(new[] { 100.0, 200.0, 400.0, 800.0, 1000.0, 1000.0 }, minimums);   // el tope (2000) fija el techo
        }

        [TestMethod]
        public void NeverExceedsTheCap_EvenWithHugeAttemptNumbers()
        {
            Assert.IsTrue(EqualJitter.Delay(500, Base, Max, () => 0.999999).TotalMilliseconds <= 2000);
        }

        [TestMethod]
        public void SpreadsDifferentCallers_SoTheyDoNotRetryAtTheSameInstant()
        {
            var random = new Random(42);
            var delays = Enumerable.Range(0, 50).Select(_ => EqualJitter.Delay(3, Base, Max, random.NextDouble).TotalMilliseconds).Distinct().Count();
            Assert.IsTrue(delays > 40, $"valores distintos: {delays}");
        }

        [TestMethod]
        public void ZeroOrNegativeAttempts_AreTreatedAsTheFirst()
        {
            Assert.AreEqual(EqualJitter.Delay(1, Base, Max, () => 0.0), EqualJitter.Delay(0, Base, Max, () => 0.0));
        }
    }

    /// <summary>SqlException no es construible en un test: se prueba la tabla de errores con un número simulado.</summary>
    internal sealed class FakeSqlNumberException : Exception
    {
        public int Number { get; }
        public FakeSqlNumberException(int number, string message = "fake sql") : base(message) => Number = number;
    }

    internal sealed class TestableClassifier : SqlDbFailureClassifier
    {
        protected override bool TryGetSqlNumber(Exception exception, out int number)
        {
            if (exception is FakeSqlNumberException f) { number = f.Number; return true; }
            number = 0;
            return false;
        }
    }

    [TestClass]
    public class SqlDbFailureClassifierTests
    {
        private readonly IDbFailureClassifier _c = new TestableClassifier();

        [TestMethod]
        public void CommandTimeout_IsHandled_ButNotRetryable()
        {
            var e = new FakeSqlNumberException(-2);
            Assert.IsTrue(_c.IsHandled(e));
            Assert.IsFalse(_c.IsRetryable(e));
        }

        [DataTestMethod]
        [DataRow(10061)]   // conexión rechazada (BD reiniciándose o sin endpoints, como en D3)
        [DataRow(-1)]      // no se pudo establecer la conexión
        [DataRow(10054)]   // conexión reseteada (BD reiniciándose)
        [DataRow(10053)]
        [DataRow(10060)]
        [DataRow(233)]
        [DataRow(64)]
        [DataRow(4060)]    // base de datos aún no disponible
        [DataRow(1205)]    // víctima de deadlock
        [DataRow(40613)]
        public void TransientConnectionErrors_AreHandledAndRetryable(int number)
        {
            var e = new FakeSqlNumberException(number);
            Assert.IsTrue(_c.IsHandled(e));
            Assert.IsTrue(_c.IsRetryable(e));
        }

        [DataTestMethod]
        [DataRow(547)]     // violación de FK
        [DataRow(2627)]    // clave duplicada
        [DataRow(102)]     // sintaxis
        [DataRow(208)]     // objeto inexistente
        public void BusinessAndProgrammingErrors_AreNotHandled(int number)
        {
            var e = new FakeSqlNumberException(number);
            Assert.IsFalse(_c.IsHandled(e));
            Assert.IsFalse(_c.IsRetryable(e));
        }

        [TestMethod]
        public void ConnectWaitTimeout_IsHandled_ButNotRetryable()
        {
            // 258: espera agotada al conectar (p. ej. paquetes descartados, como en N3). Reintentar solo sumaría timeouts.
            var e = new FakeSqlNumberException(258);
            Assert.IsTrue(_c.IsHandled(e));
            Assert.IsFalse(_c.IsRetryable(e));
        }

        [TestMethod]
        public void WrappedExceptions_AreInspectedThroughTheInnerChain()
        {
            // EF envuelve el error de SQL en DbUpdateException / InvalidOperationException
            var wrapped = new InvalidOperationException("outer", new Exception("middle", new FakeSqlNumberException(-2)));
            Assert.IsTrue(_c.IsHandled(wrapped));
            Assert.IsFalse(_c.IsRetryable(wrapped));
        }

        [TestMethod]
        public void PoolExhaustion_IsHandled_ButNotRetryable()
        {
            var e = new InvalidOperationException("Timeout expired. The timeout period elapsed prior to obtaining a connection from the pool.");
            Assert.IsTrue(_c.IsHandled(e));
            Assert.IsFalse(_c.IsRetryable(e), "con el pool agotado reintentar solo empeora la saturación");
        }

        [TestMethod]
        public void TransportLevelError_FromABrokenPooledConnection_IsRetryable()
        {
            var e = new InvalidOperationException("A transport-level error has occurred when sending the request to the server.");
            Assert.IsTrue(_c.IsHandled(e));
            Assert.IsTrue(_c.IsRetryable(e));
        }

        [DataTestMethod]
        [DataRow("A network-related or instance-specific error occurred while establishing a connection to SQL Server. (provider: TCP Provider, error: 40 - Could not open a connection to SQL Server)")]
        [DataRow("The server was not found or was not accessible.")]
        public void CanonicalConnectionFailureMessage_IsHandled_EvenWhenTheErrorNumberIsUnknown(string message)
        {
            // Respaldo por mensaje: en el cluster la primera request a la BD caída devolvía un 500 crudo
            var e = new InvalidOperationException(message);
            Assert.IsTrue(_c.IsHandled(e));
            Assert.IsFalse(_c.IsRetryable(e), "el mismo mensaje cubre una espera de conexión agotada: reintentar multiplicaría la espera");
        }

        [TestMethod]
        public void TimeoutException_IsHandled_ButNotRetryable()
        {
            Assert.IsTrue(_c.IsHandled(new TimeoutException()));
            Assert.IsFalse(_c.IsRetryable(new TimeoutException()));
        }

        [TestMethod]
        public void UnrelatedExceptions_AreIgnored()
        {
            Assert.IsFalse(_c.IsHandled(new ArgumentException("x")));
            Assert.IsFalse(_c.IsHandled(new InvalidOperationException("otra cosa")));
        }
    }

    [TestClass]
    public class DbResilienceOptionsTests
    {
        [TestMethod]
        public void Defaults_AreTheOnesDocumented()
        {
            var o = new DbResilienceOptions();
            Assert.AreEqual(5, o.CommandTimeoutSeconds);
            Assert.AreEqual(5, o.BreakerFailures);
            Assert.AreEqual(15, o.BreakDurationSeconds);
            Assert.AreEqual(3, o.RetryCount);
        }

        [TestMethod]
        public void FromConfiguration_ReadsOverrides_AndIgnoresInvalidValues()
        {
            var values = new Dictionary<string, string?>
            {
                ["Database:Resilience:CommandTimeoutSeconds"] = "3",
                ["Database:Resilience:BreakerFailures"] = "nope",
                ["Database:Resilience:RetryCount"] = "-1"
            };
            var o = DbResilienceOptions.FromConfiguration(k => values.GetValueOrDefault(k));
            Assert.AreEqual(3, o.CommandTimeoutSeconds);
            Assert.AreEqual(5, o.BreakerFailures, "valor inválido -> se conserva el por defecto");
            Assert.AreEqual(3, o.RetryCount);
        }

        [TestMethod]
        public void ApplyConnectTimeout_AddsAShortTimeout_WhenTheConnectionStringHasNone()
        {
            var cs = new DbResilienceOptions().ApplyConnectTimeout("Server=pharmago-db;Database=PharmaDb;User Id=sa;Password=x");
            StringAssert.Contains(cs, "Connect Timeout=5");
        }

        [TestMethod]
        public void ApplyConnectTimeout_RespectsAnExplicitTimeout()
        {
            var original = "Server=pharmago-db;Database=PharmaDb;Connect Timeout=40";
            StringAssert.Contains(new DbResilienceOptions().ApplyConnectTimeout(original), "40");
        }

        [TestMethod]
        public void ApplyConnectTimeout_NeverThrows_OnGarbage()
        {
            Assert.AreEqual("esto no es un connection string", new DbResilienceOptions().ApplyConnectTimeout("esto no es un connection string"));
            Assert.AreEqual("", new DbResilienceOptions().ApplyConnectTimeout(null));
        }
    }
}

using System.Linq.Expressions;
using Moq;
using PharmaGo.DataAccess.Resilience;
using PharmaGo.Domain.Entities;
using PharmaGo.Exceptions;
using PharmaGo.IDataAccess;
using Polly.CircuitBreaker;

namespace PharmaGo.Test.DataAccess.Test.Resilience
{
    internal enum Kind { Timeout, Transient, Pool }

    /// <summary>Fallo de BD de mentira: SqlException no se puede construir fuera de SqlClient.</summary>
    internal sealed class FakeDbException : Exception
    {
        public Kind Kind { get; }
        public FakeDbException(Kind kind) : base("fake " + kind) => Kind = kind;
    }

    internal sealed class FakeClassifier : IDbFailureClassifier
    {
        public bool IsHandled(Exception e) => e is FakeDbException;
        // mismo contrato que el real: solo los fallos transitorios de conexión se reintentan
        public bool IsRetryable(Exception e) => e is FakeDbException { Kind: Kind.Transient };
    }

    internal sealed class CountingObserver : IDbResilienceObserver
    {
        public int Retries, Breaks, HalfOpens, Resets;
        public readonly List<string> Registered = new();
        public readonly Dictionary<string, Func<double>> Providers = new();
        public readonly List<(string Target, bool Handled, bool Retryable)> Failures = new();
        public void Register(string target, Func<double> stateProvider) { Registered.Add(target); Providers[target] = stateProvider; }
        public void OnFailure(string target, Exception exception, bool handled, bool retryable) => Failures.Add((target, handled, retryable));
        public void OnRetry(string target) => Retries++;
        public void OnBreak(string target) => Breaks++;
        public void OnHalfOpen(string target) => HalfOpens++;
        public void OnReset(string target) => Resets++;
    }

    [TestClass]
    public class ResilientRepositoryTests
    {
        private CountingObserver _observer = null!;
        private DbResiliencePolicies _policies = null!;
        private Mock<IRepository<Drug>> _drugs = null!;
        private Mock<IRepository<Pharmacy>> _pharmacies = null!;
        private ResilientRepository<Drug> _drugRepo = null!;
        private ResilientRepository<Pharmacy> _pharmacyRepo = null!;
        private int _drugCalls;

        [TestInitialize]
        public void Setup()
        {
            _observer = new CountingObserver();
            var options = new DbResilienceOptions
            {
                BreakerFailures = 3,
                BreakDurationSeconds = 1,
                RetryCount = 3,
                RetryBaseDelayMs = 1,   // las esperas reales no importan en los tests
                RetryMaxDelayMs = 2
            };
            _policies = new DbResiliencePolicies(options, new FakeClassifier(), _observer, () => 0.5);
            _drugs = new Mock<IRepository<Drug>>();
            _pharmacies = new Mock<IRepository<Pharmacy>>();
            _drugRepo = new ResilientRepository<Drug>(_drugs.Object, _policies);
            _pharmacyRepo = new ResilientRepository<Pharmacy>(_pharmacies.Object, _policies);
            _drugCalls = 0;
        }

        private void DrugsAlways(Func<Drug> behavior) =>
            _drugs.Setup(r => r.GetOneByExpression(It.IsAny<Expression<Func<Drug, bool>>>()))
                  .Returns(() => { _drugCalls++; return behavior(); });

        private static readonly Expression<Func<Drug, bool>> Any = d => true;

        // ------------------------------------------------------------------ lecturas
        [TestMethod]
        public void Read_Success_ReturnsTheValue_WithoutRetries()
        {
            DrugsAlways(() => new Drug { Id = 7 });
            Assert.AreEqual(7, _drugRepo.GetOneByExpression(Any).Id);
            Assert.AreEqual(1, _drugCalls);
            Assert.AreEqual(0, _observer.Retries);
        }

        [TestMethod]
        public void Read_TransientFailure_IsRetriedAndTheUserNeverSeesIt()
        {
            // BD que se reinicia: las dos primeras consultas fallan por conexión rota, la tercera ya funciona
            DrugsAlways(() => _drugCalls <= 2 ? throw new FakeDbException(Kind.Transient) : new Drug { Id = 1 });
            Assert.AreEqual(1, _drugRepo.GetOneByExpression(Any).Id);
            Assert.AreEqual(3, _drugCalls);
            Assert.AreEqual(2, _observer.Retries);
        }

        [TestMethod]
        public void Read_TransientFailureThatNeverEnds_StopsAfterTheConfiguredRetries()
        {
            // Umbral del breaker mayor que los intentos de una request (como en producción: 5 > 1 + 3), para aislar el retry
            var policies = new DbResiliencePolicies(
                new DbResilienceOptions { BreakerFailures = 10, RetryCount = 3, RetryBaseDelayMs = 1, RetryMaxDelayMs = 2 },
                new FakeClassifier(), _observer, () => 0.5);
            var repo = new ResilientRepository<Drug>(_drugs.Object, policies);
            DrugsAlways(() => throw new FakeDbException(Kind.Transient));

            var ex = Assert.ThrowsException<DatabaseUnavailableException>(() => repo.GetOneByExpression(Any));
            Assert.AreEqual(4, _drugCalls, "1 intento + 3 reintentos");
            Assert.AreEqual("db:Drug", ex.Target);
        }

        [TestMethod]
        public void Read_EveryRetryCountsForTheBreaker_SoARealOutageOpensItQuickly()
        {
            // Umbral 3 < 4 intentos: el breaker se abre en pleno reintento y el cuarto intento ya no toca la BD
            DrugsAlways(() => throw new FakeDbException(Kind.Transient));
            Assert.ThrowsException<DatabaseUnavailableException>(() => _drugRepo.GetOneByExpression(Any));
            Assert.AreEqual(3, _drugCalls);
            Assert.AreEqual(CircuitState.Open, _policies.StateOf("db:Drug"));
        }

        [TestMethod]
        public void Read_Timeout_IsNeverRetried()
        {
            // Reintentar contra una tabla bloqueada multiplica la espera: justo lo que C4 mostró que tumba al servicio
            DrugsAlways(() => throw new FakeDbException(Kind.Timeout));
            Assert.ThrowsException<DatabaseUnavailableException>(() => _drugRepo.GetOneByExpression(Any));
            Assert.AreEqual(1, _drugCalls);
            Assert.AreEqual(0, _observer.Retries);
        }

        [TestMethod]
        public void Read_PoolExhausted_IsNeverRetried()
        {
            DrugsAlways(() => throw new FakeDbException(Kind.Pool));
            Assert.ThrowsException<DatabaseUnavailableException>(() => _drugRepo.GetOneByExpression(Any));
            Assert.AreEqual(1, _drugCalls);
        }

        // ------------------------------------------------------------------ circuit breaker
        [TestMethod]
        public void Breaker_OpensAfterNConsecutiveFailures_ThenFailsFastWithoutTouchingTheDatabase()
        {
            DrugsAlways(() => throw new FakeDbException(Kind.Timeout));
            for (var i = 0; i < 3; i++) Assert.ThrowsException<DatabaseUnavailableException>(() => _drugRepo.GetOneByExpression(Any));
            Assert.AreEqual(3, _drugCalls);
            Assert.AreEqual(1, _observer.Breaks);
            Assert.AreEqual(CircuitState.Open, _policies.StateOf("db:Drug"));

            var ex = Assert.ThrowsException<DatabaseUnavailableException>(() => _drugRepo.GetOneByExpression(Any));
            Assert.AreEqual(3, _drugCalls, "con el circuito abierto no se consulta la BD: no se ocupa ningún hilo ni conexión");
            Assert.IsInstanceOfType(ex.InnerException, typeof(BrokenCircuitException));
            Assert.IsTrue(ex.RetryAfter > TimeSpan.Zero, "informa cuánto esperar (se traduce a Retry-After)");
        }

        [TestMethod]
        public void Breaker_IsPerEntity_ABlockedTableDoesNotTakeDownTheOthers()
        {
            // El bloqueo de Drugs abre el breaker de Drug; Pharmacy sigue respondiendo (escenario C4)
            DrugsAlways(() => throw new FakeDbException(Kind.Timeout));
            _pharmacies.Setup(r => r.GetOneByExpression(It.IsAny<Expression<Func<Pharmacy, bool>>>())).Returns(new Pharmacy { Id = 9 });

            for (var i = 0; i < 4; i++) Assert.ThrowsException<DatabaseUnavailableException>(() => _drugRepo.GetOneByExpression(Any));

            Assert.AreEqual(CircuitState.Open, _policies.StateOf("db:Drug"));
            Assert.AreEqual(9, _pharmacyRepo.GetOneByExpression(p => true).Id);
            Assert.AreEqual(CircuitState.Closed, _policies.StateOf("db:Pharmacy"));
        }

        [TestMethod]
        public void Breaker_ClosesAgain_AfterTheBreakDuration_WhenTheDatabaseRecovers()
        {
            var failing = true;
            DrugsAlways(() => failing ? throw new FakeDbException(Kind.Timeout) : new Drug { Id = 5 });
            for (var i = 0; i < 3; i++) Assert.ThrowsException<DatabaseUnavailableException>(() => _drugRepo.GetOneByExpression(Any));
            Assert.AreEqual(CircuitState.Open, _policies.StateOf("db:Drug"));

            failing = false;
            Thread.Sleep(1200);   // BreakDuration = 1 s: la próxima request es la de prueba (half-open)

            Assert.AreEqual(5, _drugRepo.GetOneByExpression(Any).Id);
            Assert.AreEqual(CircuitState.Closed, _policies.StateOf("db:Drug"));
            Assert.AreEqual(1, _observer.HalfOpens);
            Assert.AreEqual(1, _observer.Resets);
        }

        [TestMethod]
        public void Breaker_IgnoresBusinessErrors_TheyAreNeitherWrappedNorCounted()
        {
            DrugsAlways(() => throw new ResourceNotFoundException("no existe"));
            for (var i = 0; i < 10; i++) Assert.ThrowsException<ResourceNotFoundException>(() => _drugRepo.GetOneByExpression(Any));
            Assert.AreEqual(CircuitState.Closed, _policies.StateOf("db:Drug"), "un 404 no es un problema de la BD");
            Assert.AreEqual(0, _observer.Breaks);
        }

        [TestMethod]
        public void Breaker_ASuccessInTheMiddleResetsTheConsecutiveCount()
        {
            var results = new Queue<bool>(new[] { false, false, true, false, false });   // 2 fallos, éxito, 2 fallos: nunca 3 seguidos
            DrugsAlways(() => results.Dequeue() ? new Drug { Id = 1 } : throw new FakeDbException(Kind.Timeout));
            for (var i = 0; i < 5; i++)
            {
                try { _drugRepo.GetOneByExpression(Any); } catch (DatabaseUnavailableException) { }
            }
            Assert.AreEqual(CircuitState.Closed, _policies.StateOf("db:Drug"));
        }

        // ------------------------------------------------------------------ escrituras
        [TestMethod]
        public void Save_IsNeverRetried_BecauseItIsNotIdempotent()
        {
            var calls = 0;
            _drugs.Setup(r => r.Save()).Callback(() => { calls++; throw new FakeDbException(Kind.Transient); });
            Assert.ThrowsException<DatabaseUnavailableException>(() => _drugRepo.Save());
            Assert.AreEqual(1, calls, "un timeout no dice si el commit llegó a aplicarse: reintentar podría duplicar");
        }

        [TestMethod]
        public void Save_SharesTheBreakerWithTheReadsOfTheSameEntity()
        {
            _drugs.Setup(r => r.Save()).Callback(() => throw new FakeDbException(Kind.Timeout));
            DrugsAlways(() => new Drug { Id = 1 });
            for (var i = 0; i < 3; i++) Assert.ThrowsException<DatabaseUnavailableException>(() => _drugRepo.Save());

            Assert.ThrowsException<DatabaseUnavailableException>(() => _drugRepo.GetOneByExpression(Any));
            Assert.AreEqual(0, _drugCalls, "las lecturas de esa entidad también fallan rápido");
        }

        [TestMethod]
        public void Insert_Update_Delete_AreForwardedUntouched()
        {
            var drug = new Drug();
            _drugRepo.InsertOne(drug); _drugRepo.UpdateOne(drug); _drugRepo.DeleteOne(drug);
            _drugs.Verify(r => r.InsertOne(drug), Times.Once);
            _drugs.Verify(r => r.UpdateOne(drug), Times.Once);
            _drugs.Verify(r => r.DeleteOne(drug), Times.Once);
        }

        // ------------------------------------------------------------------ consultas diferidas
        private static IEnumerable<Drug> Deferred(Kind failure)
        {
            yield return new Drug { Id = 1 };
            throw new FakeDbException(failure);   // el SQL "corre" al enumerar, no al llamar al repositorio
        }

        [TestMethod]
        public void DeferredQuery_FailuresAreCaughtInsideThePolicy_NotWhenSerializingTheResponse()
        {
            _drugs.Setup(r => r.GetAllByExpression(It.IsAny<Expression<Func<Drug, bool>>>())).Returns(() => Deferred(Kind.Timeout));
            Assert.ThrowsException<DatabaseUnavailableException>(() => _drugRepo.GetAllByExpression(Any));
        }

        [TestMethod]
        public void DeferredQuery_IsReturnedAlreadyMaterialized()
        {
            _drugs.Setup(r => r.GetAllByExpression(It.IsAny<Expression<Func<Drug, bool>>>()))
                  .Returns(new[] { new Drug { Id = 1 }, new Drug { Id = 2 } }.Where(d => true));
            var result = _drugRepo.GetAllByExpression(Any);
            Assert.IsInstanceOfType(result, typeof(List<Drug>), "ya no queda ninguna consulta pendiente de ejecutar");
            Assert.AreEqual(2, result.Count());
        }

        [TestMethod]
        public void DeferredQuery_TransientFailureIsRetried()
        {
            var attempts = 0;
            _drugs.Setup(r => r.GetAllBasicByExpression(It.IsAny<Expression<Func<Drug, bool>>>()))
                  .Returns(() => ++attempts < 3 ? Deferred(Kind.Transient) : new[] { new Drug { Id = 1 } });
            Assert.AreEqual(1, _drugRepo.GetAllBasicByExpression(Any).Count());
            Assert.AreEqual(3, attempts);
        }

        // ------------------------------------------------------------------ observabilidad
        [TestMethod]
        public void Gauge_IsClosedAtStart_OpenAfterTheFailures_AndHalfOpenOnceTheBreakDurationElapses()
        {
            DrugsAlways(() => throw new FakeDbException(Kind.Timeout));
            for (var i = 0; i < 3; i++) Assert.ThrowsException<DatabaseUnavailableException>(() => _drugRepo.GetOneByExpression(Any));
            var gauge = _observer.Providers["db:Drug"];

            Assert.AreEqual(1.0, gauge(), "abierto justo después de los fallos");
            Thread.Sleep(1200);   // BreakDuration = 1 s, sin ningún tráfico nuevo
            Assert.AreEqual(0.5, gauge(), "pasó el tiempo: ya no está 'abierto', aunque nadie haya llamado todavía");
        }

        [TestMethod]
        public void Gauge_ReadsClosed_ForAnEntityThatNeverFailed()
        {
            DrugsAlways(() => new Drug { Id = 1 });
            _drugRepo.GetOneByExpression(Any);
            Assert.AreEqual(0.0, _observer.Providers["db:Drug"]());
        }

        [TestMethod]
        public void Failures_AreReportedToTheObserver_WithTheirClassification()
        {
            DrugsAlways(() => throw new FakeDbException(Kind.Timeout));
            Assert.ThrowsException<DatabaseUnavailableException>(() => _drugRepo.GetOneByExpression(Any));
            Assert.AreEqual(1, _observer.Failures.Count);
            Assert.IsTrue(_observer.Failures[0].Handled);
            Assert.IsFalse(_observer.Failures[0].Retryable, "un timeout no se reintenta");
        }

        [TestMethod]
        public void EachEntityRegistersItsOwnTarget_SoTheMetricSeriesExistsFromTheStart()
        {
            DrugsAlways(() => new Drug());
            _pharmacies.Setup(r => r.GetOneByExpression(It.IsAny<Expression<Func<Pharmacy, bool>>>())).Returns(new Pharmacy());
            _drugRepo.GetOneByExpression(Any);
            _pharmacyRepo.GetOneByExpression(p => true);
            CollectionAssert.AreEquivalent(new[] { "db:Drug", "db:Pharmacy" }, _observer.Registered);
        }
    }
}

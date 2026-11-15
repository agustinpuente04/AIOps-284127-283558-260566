using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PharmaGo.ApiGateway.RateLimiting;

namespace PharmaGo.Test.ApiGateway.Test
{
    internal sealed class FakeSessionValidator : ISessionValidator
    {
        public readonly Dictionary<Guid, SessionInfo> Valid = new();
        public int Lookups;
        public bool TryGetCached(Guid token, out SessionInfo? session) { session = null; return false; }
        public Task<SessionInfo?> ValidateAsync(Guid token, CancellationToken ct)
        {
            Interlocked.Increment(ref Lookups);
            return Task.FromResult(Valid.TryGetValue(token, out var s) ? s : null);
        }
    }

    internal static class Http
    {
        public static DefaultHttpContext Request(string ip, string path = "/api/drug", string method = "GET", string? token = null, string? body = null, string? realIpHeader = null)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            context.Request.Path = path;
            context.Request.Method = method;
            context.Response.Body = new MemoryStream();
            if (token != null) context.Request.Headers["Authorization"] = token;
            if (realIpHeader != null) context.Request.Headers["X-Real-IP"] = realIpHeader;
            if (body != null)
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Request.Body = new MemoryStream(bytes);
                context.Request.ContentLength = bytes.Length;
                context.Request.ContentType = "application/json";
            }
            return context;
        }
    }

    [TestClass]
    public class TokenBucketMiddlewareTests
    {
        private FakeSessionValidator _validator = null!;
        private TokenBucketRateLimitMiddleware _middleware = null!;
        private int _passed;

        [TestInitialize]
        public void Setup()
        {
            _validator = new FakeSessionValidator();
            _passed = 0;
            var options = new RateLimitOptions
            {
                Anonymous = new BucketOptions { Capacity = 5, RefillPerMinute = 6 },
                Authenticated = new BucketOptions { Capacity = 5, RefillPerMinute = 6 },
                SessionLookups = new BucketOptions { Capacity = 30, RefillPerMinute = 30 }
            };
            _middleware = new TokenBucketRateLimitMiddleware(
                _ => { _passed++; return Task.CompletedTask; },
                new BucketStore(1000), _validator, Options.Create(options), NullLogger<TokenBucketRateLimitMiddleware>.Instance);
        }

        private async Task<int> Send(DefaultHttpContext context)
        {
            await _middleware.InvokeAsync(context);
            return context.Response.StatusCode == 200 ? 200 : context.Response.StatusCode;
        }

        [TestMethod]
        public async Task AnonymousFlood_DoesNotBlockAnAuthenticatedUserOnTheSameIp()
        {
            var token = Guid.NewGuid();
            _validator.Valid[token] = new SessionInfo(7, "Employee");

            var statuses = new List<int>();
            for (var i = 0; i < 50; i++) statuses.Add(await Send(Http.Request("203.0.113.5")));
            Assert.AreEqual(5, statuses.Count(s => s == 200), "el cubo anónimo de la IP dejó pasar solo su ráfaga");
            Assert.AreEqual(45, statuses.Count(s => s == 429));

            // misma IP, ya agotada para anónimos: el usuario autenticado sigue pasando
            for (var i = 0; i < 5; i++)
                Assert.AreEqual(200, await Send(Http.Request("203.0.113.5", token: token.ToString())), $"request autenticado {i + 1}");
        }

        [TestMethod]
        public async Task DifferentIps_HaveIndependentBuckets()
        {
            for (var i = 0; i < 20; i++) await Send(Http.Request("203.0.113.5"));
            Assert.AreEqual(200, await Send(Http.Request("203.0.113.6")));
        }

        [TestMethod]
        public async Task AuthenticatedUser_HasOneBucketRegardlessOfIp()
        {
            var token = Guid.NewGuid();
            _validator.Valid[token] = new SessionInfo(7, "Employee");

            for (var i = 0; i < 3; i++) Assert.AreEqual(200, await Send(Http.Request("198.51.100.1", token: token.ToString())));
            for (var i = 0; i < 2; i++) Assert.AreEqual(200, await Send(Http.Request("198.51.100.2", token: token.ToString())));
            // 5 gastados entre dos IPs: el cubo es del usuario, no de la IP
            Assert.AreEqual(429, await Send(Http.Request("198.51.100.3", token: token.ToString())));
        }

        [TestMethod]
        public async Task InventedTokens_DoNotOpenNewBuckets_AndTheirLookupsAreBounded()
        {
            var statuses = new List<int>();
            for (var i = 0; i < 200; i++) statuses.Add(await Send(Http.Request("203.0.113.9", token: Guid.NewGuid().ToString())));

            Assert.AreEqual(5, statuses.Count(s => s == 200), "un GUID distinto por request no da cubo nuevo: todos son anónimos");
            Assert.IsTrue(_validator.Lookups <= 30, $"consultas a users-service: {_validator.Lookups} (tope del presupuesto por IP: 30)");
        }

        [TestMethod]
        public async Task SpoofedXRealIp_IsIgnored()
        {
            // El atacante rota X-Real-IP en cada request. Como no se usa, todas cuentan contra la IP real del socket.
            var statuses = new List<int>();
            for (var i = 0; i < 20; i++) statuses.Add(await Send(Http.Request("203.0.113.5", realIpHeader: $"10.0.{i}.1")));
            Assert.AreEqual(5, statuses.Count(s => s == 200));
        }

        [TestMethod]
        public async Task Ipv6_RotatingWithinTheSame64_DoesNotBypassTheLimit()
        {
            var statuses = new List<int>();
            for (var i = 0; i < 20; i++) statuses.Add(await Send(Http.Request($"2001:db8:1:2::{i + 1:x}")));
            Assert.AreEqual(5, statuses.Count(s => s == 200), "las 20 direcciones pertenecen al mismo /64");
        }

        [TestMethod]
        public async Task Denied_ReturnsRetryAfterAndBucketName()
        {
            for (var i = 0; i < 5; i++) await Send(Http.Request("203.0.113.5"));
            var denied = Http.Request("203.0.113.5");
            await _middleware.InvokeAsync(denied);

            Assert.AreEqual(429, denied.Response.StatusCode);
            Assert.IsTrue(int.Parse(denied.Response.Headers["Retry-After"]) >= 1);
            Assert.AreEqual("ip", denied.Response.Headers["X-RateLimit-Bucket"].ToString());
        }

        [TestMethod]
        public async Task LoginRequests_NeverSpendTheGeneralBucket()
        {
            // Regresión medida (R2): el spraying agotaba el cubo anónimo de la IP y dejaba fuera al dueño legítimo
            for (var i = 0; i < 100; i++) Assert.AreEqual(200, await Send(Http.Request("203.0.113.5", "/api/login", "POST")));
            Assert.AreEqual(100, _passed, "el cubo general no se aplica al login (lo gestiona LoginProtectionMiddleware)");
            Assert.AreEqual(200, await Send(Http.Request("203.0.113.5")), "y la navegación anónima de esa IP no se vio afectada");
        }

        [TestMethod]
        public async Task HealthMetricsAndPreflight_AreNeverLimited()
        {
            for (var i = 0; i < 50; i++)
            {
                Assert.AreEqual(200, await Send(Http.Request("203.0.113.5", "/health")));
                Assert.AreEqual(200, await Send(Http.Request("203.0.113.5", "/metrics")));
                Assert.AreEqual(200, await Send(Http.Request("203.0.113.5", method: "OPTIONS")));
            }
        }
    }

    [TestClass]
    public class LoginProtectionMiddlewareTests
    {
        private LoginProtectionMiddleware _middleware = null!;
        private int _upstreamStatus;
        private int _upstreamCalls;
        private string? _bodySeenUpstream;

        private void Build(int accountIpThreshold = 5, int accountThreshold = 20)
        {
            var options = new RateLimitOptions
            {
                Login = new LoginOptions
                {
                    FailedPerIp = new BucketOptions { Capacity = 10, RefillPerMinute = 10 },
                    AccountIpThreshold = accountIpThreshold,
                    AccountThreshold = accountThreshold
                }
            };
            var store = new BucketStore(1000);
            var guard = new LoginGuard(options.Login, 1000);
            _upstreamCalls = 0;
            _middleware = new LoginProtectionMiddleware(async context =>
            {
                _upstreamCalls++;
                _bodySeenUpstream = await new StreamReader(context.Request.Body).ReadToEndAsync();
                context.Response.StatusCode = _upstreamStatus;
            }, store, guard, Options.Create(options));
        }

        [TestInitialize]
        public void Setup() { _upstreamStatus = 400; Build(); }

        private async Task<DefaultHttpContext> Login(string ip, string user, int upstream)
        {
            _upstreamStatus = upstream;
            var context = Http.Request(ip, "/api/login", "POST", body: $"{{\"userName\":\"{user}\",\"password\":\"x\"}}");
            await _middleware.InvokeAsync(context);
            return context;
        }

        [TestMethod]
        public async Task RepeatedWrongPasswords_LockTheAccount_BeforeReachingUsersService()
        {
            for (var i = 0; i < 5; i++) Assert.AreEqual(400, (await Login("203.0.113.5", "ana", 400)).Response.StatusCode);
            var callsBefore = _upstreamCalls;

            var blocked = await Login("203.0.113.5", "ana", 400);

            Assert.AreEqual(429, blocked.Response.StatusCode);
            Assert.AreEqual("account", blocked.Response.Headers["X-RateLimit-Bucket"].ToString());
            Assert.IsTrue(int.Parse(blocked.Response.Headers["Retry-After"]) >= 1);
            Assert.AreEqual(callsBefore, _upstreamCalls, "el intento bloqueado no llegó a users-service");
        }

        [TestMethod]
        public async Task UnknownUsers_AreLockedTheSameAsRealOnes_NoAccountOracle()
        {
            for (var i = 0; i < 5; i++) await Login("203.0.113.5", "no-existe", 404);
            Assert.AreEqual(429, (await Login("203.0.113.5", "no-existe", 404)).Response.StatusCode);
        }

        [TestMethod]
        public async Task LockedAccount_DoesNotAffectOtherUsersOnTheSameIp()
        {
            for (var i = 0; i < 5; i++) await Login("203.0.113.5", "ana", 400);
            Assert.AreEqual(429, (await Login("203.0.113.5", "ana", 400)).Response.StatusCode);

            Assert.AreEqual(200, (await Login("203.0.113.5", "beto", 200)).Response.StatusCode, "otro usuario de la misma IP entra normal");
        }

        [TestMethod]
        public async Task SuccessfulLogins_NeverConsumeTheFailedAttemptsBudget()
        {
            // una oficina entera entrando a las 9:00 desde la misma IP: 50 usuarios que nunca habían entrado desde ahí
            for (var i = 0; i < 50; i++)
                Assert.AreEqual(200, (await Login("203.0.113.5", $"empleado{i}", 200)).Response.StatusCode, $"login {i + 1}");
        }

        [TestMethod]
        public async Task GarbageLoginBodies_AreRateLimited_SinceLoginSkipsTheGeneralBucket()
        {
            _upstreamStatus = 400;
            var passed = 0;
            for (var i = 0; i < 100; i++)
            {
                var context = Http.Request("203.0.113.5", "/api/login", "POST", body: "basura-" + i);
                await _middleware.InvokeAsync(context);
                if (context.Response.StatusCode != 429) passed++;
            }
            Assert.AreEqual(60, passed, "solo pasa la capacidad del cubo de requests de login; el resto recibe 429");
        }

        [TestMethod]
        public async Task KnownDevice_StillLogsIn_WhenTheIpLoginRequestBudgetIsExhausted()
        {
            Assert.AreEqual(200, (await Login("203.0.113.5", "admin", 200)).Response.StatusCode);
            for (var i = 0; i < 100; i++) await Login("203.0.113.5", $"spray{i}", 404);   // agota fallos y requests de login
            var blocked = await Login("203.0.113.5", "nuevo", 400);
            Assert.AreEqual(429, blocked.Response.StatusCode);
            for (var i = 0; i < 20; i++)
                Assert.AreEqual(200, (await Login("203.0.113.5", "admin", 200)).Response.StatusCode, $"dueño conocido, intento {i + 1}");
        }

        [TestMethod]
        public async Task PasswordSpraying_IsStoppedByTheFailedAttemptsBudgetPerIp()
        {
            // muchas cuentas distintas, una sola IP: ninguna cuenta llega al umbral, pero la IP se queda sin presupuesto
            for (var i = 0; i < 10; i++) Assert.AreEqual(400, (await Login("203.0.113.5", $"victima{i}", 400)).Response.StatusCode);
            var blocked = await Login("203.0.113.5", "victima99", 400);
            Assert.AreEqual(429, blocked.Response.StatusCode);
            Assert.AreEqual("login-ip", blocked.Response.Headers["X-RateLimit-Bucket"].ToString());

            // otra IP no se ve afectada
            Assert.AreEqual(400, (await Login("203.0.113.6", "victima0", 400)).Response.StatusCode);
        }

        [TestMethod]
        public async Task SprayingFromASharedIp_DoesNotLockOutAUserWhoAlreadyLoggedInFromThatIp()
        {
            // Regresión medida en el cluster: el dueño legítimo recibió 429 durante el spraying de otro desde su misma IP
            Assert.AreEqual(200, (await Login("203.0.113.5", "admin", 200)).Response.StatusCode, "entra una vez: dispositivo conocido");
            for (var i = 0; i < 15; i++) await Login("203.0.113.5", $"victima{i}", 404);   // agota el presupuesto de fallos de la IP

            Assert.AreEqual(429, (await Login("203.0.113.5", "usuario-nuevo", 400)).Response.StatusCode, "un desconocido sí se frena");
            Assert.AreEqual(200, (await Login("203.0.113.5", "admin", 200)).Response.StatusCode, "su dispositivo conocido sigue entrando");
        }

        [TestMethod]
        public async Task DistributedAttack_AgainstOneAccount_IsLockedGlobally_ButTheKnownDeviceStillEnters()
        {
            Build(accountIpThreshold: 100, accountThreshold: 3);

            // la dueña entra bien una vez desde su IP: queda como dispositivo conocido
            Assert.AreEqual(200, (await Login("198.51.100.1", "ana", 200)).Response.StatusCode);

            // atacantes desde 3 IPs distintas fallan contra su cuenta
            for (var i = 0; i < 3; i++) await Login($"203.0.113.{i + 10}", "ana", 400);

            Assert.AreEqual(429, (await Login("203.0.113.99", "ana", 400)).Response.StatusCode, "IP nueva: bloqueada por el límite global de la cuenta");
            Assert.AreEqual(200, (await Login("198.51.100.1", "ana", 200)).Response.StatusCode, "su dispositivo conocido no queda fuera");
        }

        [TestMethod]
        public async Task Body_IsForwardedIntact()
        {
            await Login("203.0.113.5", "ana", 400);
            Assert.AreEqual("{\"userName\":\"ana\",\"password\":\"x\"}", _bodySeenUpstream);
        }

        [TestMethod]
        public async Task UnreadableBody_IsPassedThroughToUsersService()
        {
            _upstreamStatus = 400;
            var context = Http.Request("203.0.113.5", "/api/login", "POST", body: "{esto no es json");
            await _middleware.InvokeAsync(context);
            Assert.AreEqual(1, _upstreamCalls);
        }

        [TestMethod]
        public async Task OtherEndpoints_AreNotInspected()
        {
            _upstreamStatus = 200;
            await _middleware.InvokeAsync(Http.Request("203.0.113.5", "/api/drug"));
            await _middleware.InvokeAsync(Http.Request("203.0.113.5", "/api/login", "GET"));
            Assert.AreEqual(2, _upstreamCalls);
        }

        [TestMethod]
        public async Task ServerErrors_AreNotCountedAsFailedLogins()
        {
            for (var i = 0; i < 20; i++) await Login("203.0.113.5", "ana", 500);
            Assert.AreEqual(500, (await Login("203.0.113.5", "ana", 500)).Response.StatusCode, "5xx no bloquea a nadie");
        }
    }
}

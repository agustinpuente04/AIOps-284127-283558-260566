using System.Net;
using Microsoft.AspNetCore.Http;
using PharmaGo.ApiGateway.RateLimiting;

namespace PharmaGo.Test.ApiGateway.Test
{
    [TestClass]
    public class ClientIpMiddlewareTests
    {
        private const string Nginx = "10.244.0.119";       // pod de nginx conocido
        private const string NodeBridge = "10.244.0.1";    // puente del nodo: DENTRO de la red de pods, pero no es nginx

        private static async Task<string> Resolve(TrustedProxies trusted, string peer, string? forwardedFor, string? realIp = null)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            if (forwardedFor != null) context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            if (realIp != null) context.Request.Headers["X-Real-IP"] = realIp;
            await new ClientIpMiddleware(_ => Task.CompletedTask, trusted).InvokeAsync(context);
            return context.Connection.RemoteIpAddress!.ToString();
        }

        private static TrustedProxies OnlyNginx()
        {
            var trusted = new TrustedProxies(Array.Empty<string>());
            trusted.SetAddresses(new[] { IPAddress.Parse(Nginx), IPAddress.Parse("10.244.0.126") });
            return trusted;
        }

        [TestMethod]
        public async Task PeerIsNginx_UsesTheClientIpThatNginxAppended()
        {
            Assert.AreEqual("198.51.100.7", await Resolve(OnlyNginx(), Nginx, "198.51.100.7"));
        }

        [TestMethod]
        public async Task PeerIsNginx_IgnoresWhatTheClientPutBeforeInTheHeader()
        {
            // El cliente envía "1.2.3.4"; nginx añade su $remote_addr al final. Solo cuenta la última entrada.
            Assert.AreEqual("198.51.100.7", await Resolve(OnlyNginx(), Nginx, "1.2.3.4, 9.9.9.9, 198.51.100.7"));
        }

        [TestMethod]
        public async Task PeerInsideThePodNetworkButNotNginx_CannotSpoofTheIp()
        {
            // Regresión del bug real: el puente del nodo (10.244.0.1) está dentro de 10.244.0.0/16 pero NO es nginx
            Assert.AreEqual(NodeBridge, await Resolve(OnlyNginx(), NodeBridge, "203.0.113.99"));
        }

        [TestMethod]
        public async Task UnknownPeer_CannotSpoofTheIp()
        {
            Assert.AreEqual("192.0.2.50", await Resolve(OnlyNginx(), "192.0.2.50", "203.0.113.99"));
        }

        [TestMethod]
        public async Task XRealIpHeader_IsNeverUsed_EvenFromNginx()
        {
            Assert.AreEqual("198.51.100.7", await Resolve(OnlyNginx(), Nginx, "198.51.100.7", realIp: "6.6.6.6"));
            Assert.AreEqual("192.0.2.50", await Resolve(OnlyNginx(), "192.0.2.50", null, realIp: "6.6.6.6"));
        }

        [TestMethod]
        public async Task GarbageInTheHeader_KeepsThePeerIp()
        {
            Assert.AreEqual(Nginx, await Resolve(OnlyNginx(), Nginx, "no-es-una-ip"));
        }

        [TestMethod]
        public async Task NoTrustedProxiesConfigured_TrustsNothing()
        {
            var nobody = new TrustedProxies(Array.Empty<string>());
            Assert.AreEqual(Nginx, await Resolve(nobody, Nginx, "198.51.100.7"));
        }

        [TestMethod]
        public async Task CidrIsStillSupported_ButItIsTheBroaderOption()
        {
            var cidr = new TrustedProxies(new[] { "10.244.0.0/16" });
            // con CIDR, el puente del nodo SÍ se considera de confianza: por eso no es la opción por defecto
            Assert.AreEqual("203.0.113.99", await Resolve(cidr, NodeBridge, "203.0.113.99"));
        }

        [TestMethod]
        public void SetAddresses_ReplacesTheWholeSet_WhenNginxPodsRotate()
        {
            var trusted = OnlyNginx();
            Assert.IsTrue(trusted.IsTrusted(IPAddress.Parse(Nginx)));
            trusted.SetAddresses(new[] { IPAddress.Parse("10.244.0.200") });
            Assert.IsFalse(trusted.IsTrusted(IPAddress.Parse(Nginx)), "el pod viejo deja de ser de confianza");
            Assert.IsTrue(trusted.IsTrusted(IPAddress.Parse("10.244.0.200")));
        }

        [TestMethod]
        public void Ipv4MappedAddresses_AreNormalized()
        {
            Assert.IsTrue(OnlyNginx().IsTrusted(IPAddress.Parse(Nginx).MapToIPv6()));
        }
    }
}

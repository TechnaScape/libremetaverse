using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LibreMetaverse.Http;
using LibreMetaverse.StructuredData;
using NUnit.Framework;

namespace LibreMetaverse.Tests
{
    [TestFixture]
    public class EventQueueClientTests
    {
        private GridClient _client = null!;
        private Simulator _sim = null!;
        private EventQueueClient _queue = null!;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [SetUp]
        public void SetUp()
        {
            _client = new GridClient();
            _sim = new Simulator(_client, new IPEndPoint(IPAddress.Loopback, 13), 0);
            typeof(Simulator).GetField("connected", Private)!.SetValue(_sim, true);
            _queue = new EventQueueClient(new Uri("http://localhost/event-queue"), _sim);
        }

        [TearDown]
        public void TearDown()
        {
            _queue.Dispose();
            _sim.Dispose();
            _client.Dispose();
        }

        private void Receive(HttpStatusCode status, byte[]? body, Exception? error = null)
        {
            using var response = new HttpResponseMessage(status);
            typeof(EventQueueClient).GetMethod("RequestCompletedHandler", Private)!
                .Invoke(_queue, new object?[] { response, body, error });
        }

        private static byte[] Batch(int id, OSD events) => OSDParser.SerializeLLSDXmlBytes(
            new OSDMap { ["id"] = OSD.FromInteger(id), ["events"] = events });

        private static OSD Event(string name) => new OSDMap {
            ["message"] = OSD.FromString(name), ["body"] = new OSDMap() };

        private OSD Ack => ((OSDMap)typeof(EventQueueClient)
            .GetField("_reqPayloadMap", Private)!.GetValue(_queue)!)["ack"];

        [Test]
        public async Task PollingPostsAnLLSDMapAndAcknowledgesTheReceivedBatch()
        {
            var portProbe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            string address = $"http://127.0.0.1:{port}/";
            using var server = new HttpListener();
            server.Prefixes.Add(address);
            server.Start();
            _queue.Dispose();
            _queue = new EventQueueClient(new Uri(address), _sim);
            _queue.Start();
            try
            {
                for (int request = 0; request < 2; request++)
                {
                    var context = await server.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    using var stream = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(stream);
                    OSD payload = OSDParser.DeserializeLLSDXml(stream.ToArray());
                    Assert.That(payload, Is.TypeOf<OSDMap>(),
                        "serialized XML bytes must not be serialized a second time as LLSD binary");
                    var map = (OSDMap)payload;
                    Assert.That(map["done"].AsBoolean(), Is.False);
                    if (request == 1) Assert.That(map["ack"].AsInteger(), Is.EqualTo(42));
                    byte[] reply = Batch(42, new OSDArray());
                    context.Response.ContentType = HttpCapsClient.LLSD_XML;
                    context.Response.ContentLength64 = reply.Length;
                    await context.Response.OutputStream.WriteAsync(reply);
                    context.Response.Close();
                }
            }
            finally { _queue.Stop(true); server.Stop(); }
        }

        [TestCase(HttpStatusCode.BadGateway)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        public void HttpFailurePreservesLastAcknowledgedBatch(HttpStatusCode status)
        {
            Receive(HttpStatusCode.OK, Batch(42, new OSDArray()));
            Receive(status, System.Text.Encoding.UTF8.GetBytes("<html>Gateway error</html>"));
            Assert.That(Ack.AsInteger(), Is.EqualTo(42));
        }

        [Test]
        public void InvalidEnvelopeCannotAdvanceAcknowledgement()
        {
            Receive(HttpStatusCode.OK, Batch(42, new OSDArray()));
            Receive(HttpStatusCode.OK, Batch(99, new OSDMap()));
            Assert.That(Ack.AsInteger(), Is.EqualTo(42));
        }

        [Test]
        public void MissingCapabilityStopsQueueEvenWithoutTransportException()
        {
            Receive(HttpStatusCode.NotFound, Array.Empty<byte>());
            var source = (CancellationTokenSource)typeof(EventQueueClient)
                .GetField("_queueCts", Private)!.GetValue(_queue)!;
            Assert.That(source.IsCancellationRequested, Is.True);
        }

        [Test]
        public void EventsAreDispatchedInOrderAndMalformedEventDoesNotDropRestOfBatch()
        {
            var delivered = new List<string>();
            int caller = Environment.CurrentManagedThreadId;
            var threads = new List<int>();
            _queue.OnEvent = (name, body) => {
                lock (delivered) { delivered.Add(name); threads.Add(Environment.CurrentManagedThreadId); }
            };
            Receive(HttpStatusCode.OK, Batch(7, new OSDArray {
                Event("first"), OSD.FromString("invalid"), Event("last") }));
            Assert.That(delivered, Is.EqualTo(new[] { "first", "last" }));
            Assert.That(threads, Is.All.EqualTo(caller), "dispatch must complete on the existing polling task");
            Assert.That(Ack.AsInteger(), Is.EqualTo(7));
        }

        [Test]
        public void FailingEventCallbackDoesNotDropFollowingUpdate()
        {
            var delivered = new List<string>();
            _queue.OnEvent = (name, body) => {
                if (name == "bad") throw new InvalidOperationException("test consumer");
                delivered.Add(name);
            };
            Receive(HttpStatusCode.OK, Batch(8, new OSDArray { Event("bad"), Event("next") }));
            Assert.That(delivered, Is.EqualTo(new[] { "next" }));
        }
    }
}

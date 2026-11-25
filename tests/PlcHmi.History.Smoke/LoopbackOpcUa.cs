using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Opc.Ua;
using Opc.Ua.Security.Certificates;
using Opc.Ua.Server;

internal sealed class LoopbackOpcUa(int port, bool verifyParameters = false) : IDisposable
{
    internal const string AddressSpace = "urn:prism:plc:opcua-test";
    private const string ApplicationUri = "urn:prism:plc:opcua-test:server";
    private X509Certificate2? certificate;
    private FixtureServer? active;

    public string Endpoint => $"opc.tcp://127.0.0.1:{port}/CRT";
    public FixtureNodes Nodes => active?.Nodes ?? throw new InvalidOperationException("The OPC UA fixture is offline.");

    public async Task ConnectAsync(bool reconnected = false)
    {
        if (active is not null || port is < 1 or > 65535 || Listeners().Length != 0)
            throw new InvalidOperationException("The OPC UA fixture port is unavailable.");
        certificate ??= CertificateFactory.CreateCertificate(ApplicationUri, "Prism OPC UA fixture", "CN=Prism OPC UA fixture",
            new List<string> { "127.0.0.1" }).SetNotBefore(DateTime.UtcNow.AddDays(-1)).SetLifeTime(1).SetRSAKeySize(2048).CreateForRSA();
        var configuration = new ApplicationConfiguration
        {
            ApplicationName = "Prism OPC UA fixture",
            ApplicationUri = ApplicationUri,
            ProductUri = AddressSpace,
            ApplicationType = ApplicationType.Server,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier(certificate),
                TrustedPeerCertificates = new CertificateTrustList(),
                TrustedIssuerCertificates = new CertificateTrustList(),
                RejectedCertificateStore = new CertificateStoreIdentifier(),
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = 5000 },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = new StringCollection { Endpoint },
                SecurityPolicies = new ServerSecurityPolicyCollection
                {
                    new() { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None },
                },
                UserTokenPolicies = new UserTokenPolicyCollection { new(UserTokenType.Anonymous) },
                DiagnosticsEnabled = false,
            },
            TraceConfiguration = new TraceConfiguration(),
        };
        await configuration.Validate(ApplicationType.Server);
        var replacement = new FixtureServer(reconnected, verifyParameters);
        try
        {
            replacement.Start(configuration);
            if (Listeners() is not [{ Address: var address }] || !address.Equals(IPAddress.Loopback))
                throw new InvalidOperationException("The OPC UA server must listen on IPv4 loopback only.");
            active = replacement;
        }
        catch
        {
            replacement.Dispose();
            throw;
        }
    }

    private IPEndPoint[] Listeners() => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
        .Where(endpoint => endpoint.Port == port).ToArray();

    public void Disconnect()
    {
        var server = active;
        active = null;
        if (server is null) return;
        try { server.Stop(); }
        finally { server.Dispose(); }
    }

    public void Dispose()
    {
        try { Disconnect(); }
        finally { certificate?.Dispose(); }
    }

    private sealed class FixtureServer(bool reconnected, bool verifyParameters) : StandardServer
    {
        public FixtureNodes Nodes { get; private set; } = null!;

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            Nodes = new FixtureNodes(server, configuration, reconnected, verifyParameters);
            return new MasterNodeManager(server, configuration, null, Nodes);
        }

        protected override ServerProperties LoadServerProperties() => new()
        {
            ManufacturerName = "Prism verification",
            ProductName = "Loopback process fixture",
            ProductUri = AddressSpace,
            SoftwareVersion = "1",
            BuildNumber = "1",
            BuildDate = DateTime.UtcNow,
        };
    }
}

internal sealed class FixtureNodes(IServerInternal server, ApplicationConfiguration configuration, bool reconnected, bool verifyParameters)
    : CustomNodeManager2(server, configuration, LoopbackOpcUa.AddressSpace)
{
    internal const string StopBankNode = "Fixture.System.st_UnitMode[0].b_Action";
    private readonly Dictionary<string, BaseDataVariableState> values = new(StringComparer.Ordinal);
    private readonly List<bool> stopWrites = [];
    private readonly List<(string Key, object Value)> parameterWrites = [];
    private readonly Dictionary<string, ParameterPoint> writablePoints = new(StringComparer.Ordinal);
    private int rejectedWrites;

    public (bool[] Stop, int Rejected) WriteTrace()
    {
        lock (Lock) return (stopWrites.ToArray(), rejectedWrites);
    }

    public bool[] ActionBank()
    {
        lock (Lock) return ((bool[])values[StopBankNode].Value).ToArray();
    }

    public (string Key, object Value)[] ParameterWrites()
    {
        lock (Lock) return parameterWrites.ToArray();
    }

    public void SetMode(short mode, short status, bool debugRequest = false)
    {
        lock (Lock)
        {
            Set("i_Mode", mode);
            Set("i_Status", status);
            var actions = ((bool[])values[StopBankNode].Value).ToArray();
            actions[6] = debugRequest;
            Set(StopBankNode, actions);
            var feedback = new bool[6];
            feedback[5] = status == 70;
            Set("b_Signal", feedback);
        }
    }

    private void Set(string name, object value)
    {
        var node = values[name];
        node.Value = value;
        node.Timestamp = DateTime.UtcNow;
        node.ClearChangeMasks(SystemContext, false);
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            var folder = new FolderState(null)
            {
                NodeId = new NodeId("Fixture", NamespaceIndex),
                BrowseName = new QualifiedName("Fixture", NamespaceIndex),
                DisplayName = "Fixture",
                TypeDefinitionId = ObjectTypeIds.FolderType,
                ReferenceTypeId = ReferenceTypeIds.Organizes,
            };
            folder.AddReference(ReferenceTypeIds.Organizes, true, ObjectIds.ObjectsFolder);
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();
            references.Add(new NodeStateReference(ReferenceTypeIds.Organizes, false, folder.NodeId));

            AddVariable(folder, "i_Mode", (short)0);
            AddVariable(folder, "i_Status", (short)0);
            AddVariable(folder, StopBankNode, new bool[9], AccessLevels.CurrentReadOrWrite);
            AddVariable(folder, "b_Signal", new bool[6]);
            var input = new bool[16];
            input[0] = !reconnected;
            input[1] = reconnected;
            AddVariable(folder, "DI", input);
            var output = new bool[16];
            output[0] = true;
            AddVariable(folder, "DO", output);
            if (verifyParameters)
            {
                foreach (var point in ParameterPoint.All)
                {
                    bool writable = point.Command is not null || point.WriteMode is not null;
                    AddVariable(folder, point.Node, point.InitialValue(),
                        writable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead);
                    if (writable) writablePoints.Add(point.Node, point);
                }
            }
            AddPredefinedNode(SystemContext, folder);
        }
    }

    public override void Write(OperationContext context, IList<WriteValue> requests, IList<ServiceResult> results)
    {
        lock (Lock)
        {
            for (int index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                if (request.Processed || request.NodeId.NamespaceIndex != NamespaceIndex) continue;
                request.Processed = true;
                if (request.NodeId.Identifier is StopBankNode && (request.IndexRange == "3" || verifyParameters && request.IndexRange == "6") &&
                    request.AttributeId == Attributes.Value && request.Value.Value is bool[] { Length: 1 } value)
                {
                    var bank = ((bool[])values[StopBankNode].Value).ToArray();
                    int bit = request.IndexRange == "3" ? 3 : 6;
                    bank[bit] = value[0];
                    Set(StopBankNode, bank);
                    if (bit == 3) stopWrites.Add(value[0]);
                    results[index] = ServiceResult.Good;
                }
                else if (request.NodeId.Identifier is string node && writablePoints.TryGetValue(node, out var point) &&
                    string.IsNullOrEmpty(request.IndexRange) && request.AttributeId == Attributes.Value &&
                    request.Value.Value is { } scalar && scalar.GetType() == values[node].Value.GetType())
                {
                    Set(node, scalar);
                    parameterWrites.Add((point.Key, scalar));
                    results[index] = ServiceResult.Good;
                }
                else
                {
                    rejectedWrites++;
                    results[index] = StatusCodes.BadNotWritable;
                }
            }
        }
        base.Write(context, requests, results);
    }

    private void AddVariable(NodeState folder, string name, object value, byte access = AccessLevels.CurrentRead)
    {
        var node = new BaseDataVariableState(folder)
        {
            NodeId = new NodeId(name, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
            SymbolicName = name,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            ReferenceTypeId = ReferenceTypeIds.Organizes,
            DataType = Opc.Ua.TypeInfo.GetDataTypeId(value),
            ValueRank = value is Array ? ValueRanks.OneDimension : ValueRanks.Scalar,
            AccessLevel = access,
            UserAccessLevel = access,
            Value = value,
            Timestamp = DateTime.UtcNow,
            StatusCode = StatusCodes.Good,
        };
        if (value is Array array) node.ArrayDimensions = new Opc.Ua.ReadOnlyList<uint>([(uint)array.Length]);
        values.Add(name, node);
        folder.AddChild(node);
    }
}

internal sealed record ParameterPoint(string Key, string Type, JsonElement Initial, string? Command, string? WriteMode, bool WriteOnly)
{
    public string Node => "Fixture." + Key;

    public object InitialValue() => Type switch
    {
        "Bool" => Initial.GetBoolean(),
        "Double" => Initial.GetDouble(),
        "Int16" => Initial.GetInt16(),
        "Int32" => Initial.GetInt32(),
        _ => throw new InvalidOperationException($"Unsupported fixture type: {Type}."),
    };

    public static IReadOnlyList<ParameterPoint> All { get; } = Load();

    private static ParameterPoint[] Load()
    {
        using var json = typeof(ParameterPoint).Assembly.GetManifestResourceStream("ParameterPoints.json")!;
        return JsonSerializer.Deserialize<ParameterPoint[]>(json)!;
    }
}

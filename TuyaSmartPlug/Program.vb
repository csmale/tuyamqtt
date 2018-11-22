Imports System
Imports System.Xml
Imports System.IO
Imports System.Reflection
Imports m4rcus.TuyaCore
Imports uPLibrary.Networking.M2Mqtt



''' <summary>
''' Uses TuyaCore component from https://github.com/Marcus-L/m4rcus.TuyaCore
''' </summary>

Class TuyaConfig
    Public Topic As String
    Public Name As String
    Public Dev As New TuyaPlug
    Friend Thread As Threading.Thread
    Friend Watchdog As DateTime
End Class

Module TuyaMqtt
    Const MIN_STARTUP_DELAY As Integer = 1000
    Dim StartupDelay As Integer = 1000    ' delay between thread starts
    Const MIN_POLL_DELAY As Integer = 1000
    Dim PollDelay As Integer = 1000       ' delay between cancelled checkes
    Const MIN_SCAN_DELAY As Integer = 1000
    Dim ScanDelay As Integer = 5000       ' delay between readings for each device

    Dim bVerbose As Boolean = True

    Dim devices As New List(Of TuyaConfig)
    Dim _cancel As Boolean

    Dim mqtt As MqttClient
    Dim mqttServer As String = "localhost"
    Dim mqttPort As Integer = 1883
    Dim mqttUser As String = ""
    Dim mqttPassword As String = ""
    Dim mqttClientID As String = ""
    Dim mqttQos As Integer = 1
    Dim mqttRetain As Boolean = False

    ReadOnly mqttLock As New Object

    Function GetProgVersion() As String
        Dim execAssembly = Assembly.GetCallingAssembly()
        Dim name = execAssembly.GetName()
        Dim sTmp As String

        sTmp = $"{name.Name} {name.Version.Major.ToString():0}.{name.Version.Minor.ToString():0}.{name.Version.Build.ToString():0} for .Net {execAssembly.ImageRuntimeVersion}"
        Return sTmp
    End Function
    Sub Main(args As String())

        Dim xDoc As New XmlDocument()

        Console.WriteLine(GetProgVersion())

        For Each sArg In args
            Select Case sArg
                Case "-v", "--verbose"
                    bVerbose = True
            End Select
        Next

        xDoc.Load(Path.Combine(ApplicationPath(), "config.xml"))
        Dim xConfig As XmlElement = xDoc.SelectSingleNode("TuyaMqtt")
        Integer.TryParse(NodeText(xConfig.SelectSingleNode("Scan/ScanDelay")), ScanDelay)
        ScanDelay = Math.Max(ScanDelay, MIN_SCAN_DELAY)
        Integer.TryParse(NodeText(xConfig.SelectSingleNode("Scan/StartupDelay")), StartupDelay)
        StartupDelay = Math.Min(StartupDelay, MIN_STARTUP_DELAY)
        Integer.TryParse(NodeText(xConfig.SelectSingleNode("Scan/PollDelay")), PollDelay)
        PollDelay = Math.Max(PollDelay, MIN_POLL_DELAY)

        Dim xMqtt As XmlElement = xConfig.SelectSingleNode("Mqtt")
        mqttServer = NodeText(xMqtt.SelectSingleNode("Server"))
        Integer.TryParse(Integer.Parse(NodeText(xMqtt.SelectSingleNode("Port"))), mqttPort)
        mqttClientID = NodeText(xMqtt.SelectSingleNode("ClientID"))
        mqttUser = NodeText(xMqtt.SelectSingleNode("User"))
        mqttPassword = NodeText(xMqtt.SelectSingleNode("Password"))
        Dim bMqttSecure As Boolean = Boolean.Parse(NodeText(xMqtt.SelectSingleNode("Secure")))
        Integer.TryParse(NodeText(xMqtt.SelectSingleNode("QOS")), mqttQos)
        Boolean.TryParse(NodeText(xMqtt.SelectSingleNode("Retain")), mqttRetain)
        mqtt = New MqttClient(mqttServer, mqttPort, bMqttSecure, Nothing, Nothing,
                              MqttSslProtocols.None)

        For Each xDev As XmlElement In xDoc.SelectNodes("TuyaMqtt/Devices/Device")
            Dim d As New TuyaConfig
            With d
                .Topic = NodeText(xDev.SelectSingleNode("Topic"))
                .Name = NodeText(xDev.SelectSingleNode("Name"))
                .Dev.IP = NodeText(xDev.SelectSingleNode("IP"))
                If .Name.Length = 0 Then .Name = .Dev.IP
                .Dev.LocalKey = NodeText(xDev.SelectSingleNode("LocalKey"))
                .Dev.Id = NodeText(xDev.SelectSingleNode("ID"))
                If .Dev.IP.Length * .Dev.LocalKey.Length * .Dev.Id.Length * .Topic.Length = 0 Then
                    Console.WriteLine("Missing value in config: one of IP, LocalKey, Id, Topic not specified.")
                Else
                    devices.Add(d)
                End If
            End With
        Next

        If devices.Count = 0 Then
            Console.WriteLine("No devices found, exiting.")
            Return
        End If

        Console.WriteLine($"Found {devices.Count} devices.")
        For Each d In devices
            Console.WriteLine($"IP: {d.Dev.IP} Name: {d.Name} on topic: {d.Topic}")
        Next

        Console.TreatControlCAsInput = False
        AddHandler Console.CancelKeyPress, AddressOf HandleBreak

        For Each d In devices
            Try
                Dim thr = New Threading.Thread(Sub() LoopSingle(d))
                thr.IsBackground = True
                d.Thread = thr
                d.Thread.Name = d.Name
                d.Watchdog = DateTime.UtcNow()
                thr.Start()
                Console.WriteLine($"Thread {thr.ManagedThreadId} started for {d.Name}")
                Threading.Thread.Sleep(StartupDelay) '  To stagger the operations a bit
            Catch e As Exception
                Console.WriteLine($"Exception starting thread {d.Thread.ManagedThreadId} for {d.Name}: {e.Message}")
            End Try
        Next

        _cancel = False
        While Not _cancel
            Threading.Thread.Sleep(PollDelay)
            Dim watchdogNow As DateTime = DateTime.UtcNow()
            For Each d In devices
                Dim wdint As TimeSpan = watchdogNow.Subtract(d.Watchdog)
                If wdint.TotalMilliseconds > (10 * ScanDelay) Then
                    Console.WriteLine($"Thread {d.Thread.ManagedThreadId} for {d.Name} is not responding, interrupting")
                    Try
                        d.Thread.Interrupt()
                    Catch e As Exception
                        Console.WriteLine($"Exception interrupting thread {d.Thread.ManagedThreadId} for {d.Name}: {e.Message}")
                    End Try
                ElseIf Not d.Thread.IsAlive Then
                    Console.WriteLine($"Thread {d.Thread.ManagedThreadId} for {d.Name} has died, restarting, state={d.Thread.ThreadState}")
                    Try
                        d.Thread = New Threading.Thread(Sub() LoopSingle(d))
                        d.Thread.IsBackground = True
                        d.Thread.Name = d.Name
                        d.Thread.Start()
                        Console.WriteLine($"Thread {d.Thread.ManagedThreadId} started for {d.Name}")
                        Threading.Thread.Sleep(StartupDelay) '  To stagger the operations a bit
                    Catch e As Exception
                        Console.WriteLine($"Exception starting thread {d.Thread.ManagedThreadId} for {d.Name}: {e.Message}")
                    End Try
                End If
            Next
        End While

        For Each d In devices
            Try
                d.Thread.Abort()
                d.Thread.Join()
            Catch e As Exception
                Console.WriteLine($"Exception collecting thread {d.Thread.ManagedThreadId} for {d.Name}: {e.Message}")
            End Try
        Next
    End Sub
    ''' <summary>
    ''' Loop for a single device. Get status and post, then sleep and repeat.
    ''' </summary>
    ''' <param name="tp"></param>
    Private Sub LoopSingle(tp As TuyaConfig)
        Dim stat As TuyaStatus
        Console.WriteLine($"LoopSingle entered for {tp.Name} on thread {Threading.Thread.CurrentThread.Name} #{Threading.Thread.CurrentThread.ManagedThreadId}")
        While True
            If _cancel Then Exit While
            ' Threading.Thread.Sleep(ScanDelay)
            stat = QuerySingle(tp)
            tp.Watchdog = Date.UtcNow()
        End While
        Console.WriteLine($"LoopSingle thread #{Threading.Thread.CurrentThread.ManagedThreadId} complete for {tp.Name}")
    End Sub
    Function QuerySingle(tp As TuyaConfig) As TuyaStatus
        Dim res As TuyaStatus
        Dim sMsg As String
        Dim msg() As Byte

        Threading.Thread.Sleep(ScanDelay)
        Try
            Dim x As Task(Of TuyaStatus)
            x = tp.Dev.GetStatus()
            res = x.Result
            With res
                .Power_W /= 10
                ' If bVerbose Then Console.WriteLine($"Powered { .Powered}, Voltage { .Voltage_V}V, Current { .Current_mA}mA, Power { .Power_W}W")
                sMsg = FormatMqttMessage(res, tp.Topic)
                msg = Text.Encoding.UTF8.GetBytes(sMsg)
                SyncLock mqttLock
                    If Not mqtt.IsConnected Then
                        mqtt.Connect(mqttClientID, mqttUser, mqttPassword)
                    End If
                    If mqtt.IsConnected Then
                        mqtt.Publish(tp.Topic, msg, mqttQos, True)
                    Else
                        Console.WriteLine($"Unable to connect to MQTT on {mqttServer}:{mqttPort} for topic {tp.Topic}")
                        ' mqtt problem...
                    End If
                End SyncLock
            End With
            Return res
        Catch e As Exception
            Console.WriteLine($"Error from {tp.Name}: {e.Message}")
            Return Nothing
        End Try
    End Function
    Private Function FormatMqttMessage(res As TuyaStatus, topic As String) As String
        Dim ts As String = DateTime.UtcNow.ToString("o")
        Dim m As String = "{" &
            JSonString("sensor", topic) & "," &
            JSonString("timestamp", ts) & "," &
            JSonVar("onoff", If(res.Powered, 1, 0)) & "," &
            JSonVar("voltage", res.Voltage_V.ToString()) & "," &
            JSonVar("current", res.Current_mA.ToString()) & "," &
            JSonVar("power", res.Power_W.ToString()) &
            "}"
        If bVerbose Then Console.WriteLine(m)
        Return m
    End Function
    Private Function JSonString(var As String, val As String) As String
        Return Quoted(var) & ":" & Quoted(val)
    End Function
    Private Function JSonVar(var As String, val As String) As String
        Return Quoted(var) & ":" & val
    End Function
    Private Function Quoted(s As String) As String
        Return """" & s & """"
    End Function
    Private Function NodeText(x As XmlElement) As String
        If x Is Nothing Then
            Return ""
        Else
            Return x.InnerText
        End If
    End Function
    Private Function ApplicationPath() As String
        Return Path.GetDirectoryName([Assembly].GetEntryAssembly().Location)
    End Function
    Sub HandleBreak(ByVal sender As Object, ByVal args As ConsoleCancelEventArgs)
        Console.WriteLine("Cancelling...")
        _cancel = True
        For Each d In devices
            If (d.Thread.ThreadState And Threading.ThreadState.WaitSleepJoin) <> 0 Then
                d.Thread.Interrupt()
            End If
            d.Thread.Abort()
        Next
    End Sub
End Module

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
    Public Dev As New TuyaPlug
End Class

Module TuyaMqtt
    Dim StartupDelay As Integer = 1000    ' delay between thread starts
    Dim PollDelay As Integer = 1000       ' delay between cancelled checkes
    Dim ScanDelay As Integer = 5000       ' delay between readings for each device

    Dim bVerbose As Boolean = False

    Dim _cancel As Boolean

    Dim mqtt As MqttClient
    Dim mqttServer As String = "localhost"
    Dim mqttPort As Integer = 1883
    Dim mqttUser As String = ""
    Dim mqttPassword As String = ""
    Dim mqttClientID As String = ""
    Dim mqttQos As Integer = 1
    Dim mqttRetain As Boolean = False

    Dim mqttLock As New Object
    Sub Main(args As String())
        Dim devices As New List(Of TuyaConfig)
        Dim xDoc As New XmlDocument()

        For Each sArg In args
            Select Case sArg
                Case "-v", "--verbose"
                    bVerbose = True
            End Select
        Next

        xDoc.Load(Path.Combine(ApplicationPath(), "config.xml"))
        Dim xConfig As XmlElement = xDoc.SelectSingleNode("TuyaMqtt")
        Integer.TryParse(NodeText(xConfig.SelectSingleNode("Scan/ScanDelay")), ScanDelay)
        Integer.TryParse(NodeText(xConfig.SelectSingleNode("Scan/StartupDelay")), StartupDelay)
        Integer.TryParse(NodeText(xConfig.SelectSingleNode("Scan/PollDelay")), PollDelay)

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
                .Dev.IP = NodeText(xDev.SelectSingleNode("IP"))
                .Dev.LocalKey = NodeText(xDev.SelectSingleNode("LocalKey"))
                .Dev.Id = NodeText(xDev.SelectSingleNode("ID"))
                If .Dev.IP.Length * .Dev.LocalKey.Length * .Dev.Id.Length * .Topic.Length = 0 Then
                    Console.WriteLine("Missing value in config")
                    Exit Sub
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
            Console.WriteLine($"IP: {d.Dev.IP} on topic: {d.Topic}")
        Next

        Console.TreatControlCAsInput = False
        AddHandler Console.CancelKeyPress, AddressOf HandleBreak

        Dim t As New List(Of Threading.Thread)

        For Each d In devices
            Dim thr = New Threading.Thread(Sub() LoopSingle(d))
            t.Add(thr)
            thr.Start()
            Threading.Thread.Sleep(StartupDelay) '  To stagger the operations a bit
        Next

        _cancel = False
        While Not _cancel
            Threading.Thread.Sleep(PollDelay)
        End While

        For Each thr In t
            thr.Abort()
            thr.Join()
        Next
    End Sub
    Private Sub LoopSingle(tp As TuyaConfig)
        While True
            QuerySingle(tp)
            Threading.Thread.Sleep(ScanDelay)
            If _cancel Then Exit While
        End While
    End Sub
    Async Function QuerySingle(tp As TuyaConfig) As Task(Of TuyaStatus)
        Dim res As TuyaStatus
        Dim sMsg As String
        Dim msg() As Byte
        Try
            res = Await tp.Dev.GetStatus()
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
                        Console.WriteLine($"Unable to connect to MQTT on {mqttServer}:{mqttPort}")
                        ' mqtt problem...
                    End If
                End SyncLock
            End With
            Return res
        Catch e As Exception
            Console.WriteLine($"Error: {e.Message}")
            Return Nothing
        End Try
    End Function
    Private Function FormatMqttMessage(res As TuyaStatus, topic As String) As String
        Dim m As String = "{" &
            JSonString("sensor", topic) & "," &
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
        Threading.Thread.CurrentThread.Abort()
        _cancel = True
    End Sub
End Module

using System;

namespace DadBoard.Agent;

public sealed class AgentState
{
    public string PcId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string LastHelloTs { get; set; } = "";
    public string LastWsMessageTs { get; set; } = "";
    public int WsClientCount { get; set; }
    public string LastCommandId { get; set; } = "";
    public string LastCommandType { get; set; } = "";
    public string LastCommandTs { get; set; } = "";
    public string LastCommandState { get; set; } = "";
}

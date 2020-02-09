namespace WRPServer.Network.Enum
{
    public enum ClientState
    {
        IDLE = 0,
        CONNECTED = 1,
        GET_CAMERA_LIST = 2,
        OPEN_CAMERA = 3,
        CLOSE_CAMERA = 4,
        CAMERA_SELECTED = 5,
        GET_FRAME = 6,
        START_CONTINUOUS_GRABBING = 7,
        STOP_CONTINUOUS_GRABBING = 8,
        CONTINUOUS_GRABBING = 9,
    }
}

namespace WRPServer.Network.Enum
{
    public enum MessageType
    {
        OK = 1,
        ERROR = 2,
        GET_CAMERA_LIST = 3,
        CAMERA_LIST = 4,
        OPEN_CAMERA = 5,
        CLOSE_CAMERA = 6,
        GET_FRAME = 7,
        FRAME = 8,
        START_CONTINUOUS_GRABBING = 9,
        STOP_CONTINUOUS_GRABBING = 10,
        ACK_CONTINUOUS_GRABBING = 11,
        NOT_ASSIGNED = 255
    }
    
    public enum ErrorCode
    {
        UNEXPECTED_MESSAGE = 0,
        CAMERA_NOT_FOUND = 1,
        CAMERA_NOT_RESPONDING = 2,
        CAMERA_NOT_OPEN = 3,
        CAMERA_NOT_CONNECTED = 4,
        CAMERA_NOT_ACQUIRING = 5,
        NOT_ASSIGNED = 255
    }
}

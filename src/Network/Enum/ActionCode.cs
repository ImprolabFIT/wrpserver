namespace WRPServer.Network.Enum
{
    public enum ActionCode
    {
        HELLO = 0,
        CLOSE = 1,
        LIST = 2,
        LOCK_CAMERA = 3,
        UNLOCK_CAMERA = 4,
        GET_SETTINGS = 5,
        SET_SETTINGS = 6,
        SINGLE_FRAME = 7,
        CONTINOUS_GRAB = 8,
        ACK_CONTINUOUS_GRAB = 9,
        STOP_CONTINUOS_GRAB = 10

    }
}

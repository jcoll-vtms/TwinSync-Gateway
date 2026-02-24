import time
import threading

# Import the actual CLI main function (this matches your traceback path)
from cpppo.server.enip.main import main as enip_main

TAG_MOTOR_RUNNING = "Program:MainProgram.MotorRunning"
TAG_PART_COUNT    = "Program:MainProgram.PartCount"

tags = {
    TAG_MOTOR_RUNNING: True,  # BOOL
    TAG_PART_COUNT: 0,        # DINT
}

def update_tags():
    while True:
        tags[TAG_PART_COUNT] += 1
        if tags[TAG_PART_COUNT] % 5 == 0:
            tags[TAG_MOTOR_RUNNING] = not tags[TAG_MOTOR_RUNNING]
        time.sleep(1)

if __name__ == "__main__":
    threading.Thread(target=update_tags, daemon=True).start()

    # Start server with a live-updating dict
    # Note: address format for enip_main is usually a string "host:port"
    enip_main(argv=[
        "--address", "0.0.0.0:44818",
        f"{TAG_MOTOR_RUNNING}=BOOL",
        f"{TAG_PART_COUNT}=DINT",
    ], tags=tags)

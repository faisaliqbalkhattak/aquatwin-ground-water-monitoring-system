"""Thread-safe bridge from Paho MQTT callbacks to async WebSocket clients."""

from __future__ import annotations

import asyncio
from threading import Lock


class UpdateBroker:
    def __init__(self) -> None:
        self._subscribers: set[asyncio.Queue[int]] = set()
        self._version = 0
        self._lock = Lock()

    @property
    def version(self) -> int:
        with self._lock:
            return self._version

    def subscribe(self) -> asyncio.Queue[int]:
        queue: asyncio.Queue[int] = asyncio.Queue(maxsize=1)
        self._subscribers.add(queue)
        return queue

    def unsubscribe(self, queue: asyncio.Queue[int]) -> None:
        self._subscribers.discard(queue)

    def publish(self) -> None:
        with self._lock:
            self._version += 1
            version = self._version
        for queue in tuple(self._subscribers):
            if queue.full():
                try:
                    queue.get_nowait()
                except asyncio.QueueEmpty:
                    pass
            queue.put_nowait(version)

    def publish_threadsafe(self, loop: asyncio.AbstractEventLoop) -> None:
        loop.call_soon_threadsafe(self.publish)

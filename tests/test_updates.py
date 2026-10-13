import asyncio

from aquatwin_backend.updates import UpdateBroker


def test_update_broker_notifies_subscribers_and_tracks_version() -> None:
    async def scenario() -> None:
        broker = UpdateBroker()
        queue = broker.subscribe()
        broker.publish()
        assert await asyncio.wait_for(queue.get(), timeout=1) == 1
        assert broker.version == 1
        broker.unsubscribe(queue)

    asyncio.run(scenario())


def test_update_broker_keeps_only_latest_pending_version() -> None:
    async def scenario() -> None:
        broker = UpdateBroker()
        queue = broker.subscribe()
        broker.publish()
        broker.publish()
        assert await asyncio.wait_for(queue.get(), timeout=1) == 2

    asyncio.run(scenario())

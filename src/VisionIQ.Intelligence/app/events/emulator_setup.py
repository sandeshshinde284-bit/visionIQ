"""Create the FND-05 topics and push subscriptions in the LOCAL Pub/Sub emulator.

    python -m app.events.emulator_setup

Refuses to run unless PUBSUB_EMULATOR_HOST is set, so it can never touch a real project. Cloud
topics, subscriptions and dead-letter policies come from scripts/pubsub/provision-pubsub.ps1.
Rerunnable: existing resources are left as they are.
"""

import os
import sys
import time
from collections.abc import Callable

from google.api_core import exceptions
from google.cloud import pubsub_v1

from app.events.contracts import find_contracts_dir
from app.events.topology import load_topology

PUSH_BASE_URLS = {
    "api": os.environ.get("EVENTS_PUSH_BASE_URL_API", "http://api:5100"),
    "intelligence": os.environ.get("EVENTS_PUSH_BASE_URL_INTELLIGENCE", "http://intelligence:8000"),
}


def _create(action: str, name: str, call: Callable[[], object]) -> None:
    try:
        call()
        print(f"created {action} {name}")
    except exceptions.AlreadyExists:
        print(f"exists  {action} {name}")


def main() -> None:
    if not os.environ.get("PUBSUB_EMULATOR_HOST"):
        sys.exit("PUBSUB_EMULATOR_HOST is not set; refusing to create resources in a real project.")
    project = os.environ.get("PUBSUB_PROJECT_ID", "visioniq-local")
    topology = load_topology(find_contracts_dir())
    publisher = pubsub_v1.PublisherClient()
    subscriber = pubsub_v1.SubscriberClient()

    for attempt in range(30):  # the emulator takes a few seconds to accept connections
        try:
            list(publisher.list_topics(request={"project": f"projects/{project}"}, timeout=5))
            break
        except exceptions.GoogleAPICallError:
            if attempt == 29:
                raise
            time.sleep(2)

    for route in topology.routes:
        topic = publisher.topic_path(project, route.topic)
        dead_letter = publisher.topic_path(project, route.dead_letter_topic)
        _create("topic", route.topic, lambda t=topic: publisher.create_topic(name=t))
        _create(
            "topic", route.dead_letter_topic, lambda t=dead_letter: publisher.create_topic(name=t)
        )
        endpoint = PUSH_BASE_URLS[route.consumer] + topology.push_path
        # The emulator does not enforce dead-lettering; the policy is still recorded so local and
        # cloud subscriptions are described by the same topology file.
        request = {
            "name": subscriber.subscription_path(project, route.subscription),
            "topic": topic,
            "push_config": {"push_endpoint": endpoint},
            "ack_deadline_seconds": topology.retry["ack_deadline_seconds"],
            "retry_policy": {
                "minimum_backoff": {"seconds": topology.retry["minimum_backoff_seconds"]},
                "maximum_backoff": {"seconds": topology.retry["maximum_backoff_seconds"]},
            },
            "dead_letter_policy": {
                "dead_letter_topic": dead_letter,
                "max_delivery_attempts": topology.retry["max_delivery_attempts"],
            },
        }
        _create(
            "subscription",
            f"{route.subscription} -> {endpoint}",
            lambda r=request: subscriber.create_subscription(request=r),
        )


if __name__ == "__main__":
    main()

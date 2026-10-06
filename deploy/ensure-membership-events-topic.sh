#!/usr/bin/env bash
set -Eeuo pipefail

topic="${KAFKA_TOPIC:-s101.xanhnow.membership.events}"
bootstrap="${KAFKA_BOOTSTRAP_SERVER:-192.168.2.14:9092}"
partitions="${KAFKA_TOPIC_PARTITIONS:-3}"
replication_factor="${KAFKA_TOPIC_REPLICATION_FACTOR:-3}"

find_kafka_topics() {
    if command -v kafka-topics.sh >/dev/null 2>&1; then
        command -v kafka-topics.sh
        return
    fi

    for candidate in \
        /opt/kafka/bin/kafka-topics.sh \
        /usr/local/kafka/bin/kafka-topics.sh \
        /usr/share/kafka/bin/kafka-topics.sh \
        /opt/bitnami/kafka/bin/kafka-topics.sh; do
        if [[ -x "$candidate" ]]; then
            printf '%s\n' "$candidate"
            return
        fi
    done
}

kafka_topics="$(find_kafka_topics)"
if [[ -z "$kafka_topics" ]]; then
    echo "kafka_topics_not_found" >&2
    exit 1
fi

if "$kafka_topics" \
    --bootstrap-server "$bootstrap" \
    --describe \
    --topic "$topic" >/tmp/xanhnow-membership-topic.describe 2>/dev/null; then
    cat /tmp/xanhnow-membership-topic.describe
    echo "status=PASS action=existing kafka_topic=$topic"
    exit 0
fi

echo "topic_missing=true kafka_topic=$topic"

"$kafka_topics" \
    --bootstrap-server "$bootstrap" \
    --create \
    --if-not-exists \
    --topic "$topic" \
    --partitions "$partitions" \
    --replication-factor "$replication_factor" \
    --config min.insync.replicas=2

"$kafka_topics" \
    --bootstrap-server "$bootstrap" \
    --describe \
    --topic "$topic"

echo "status=PASS action=created kafka_topic=$topic partitions=$partitions replication_factor=$replication_factor"

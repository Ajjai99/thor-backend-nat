output "queue_arn" {
  description = "The gate's ingress queue. Thor.TaskApi sends a message here when a task reaches a terminal status."
  value       = aws_sqs_queue.gate.arn
}

output "queue_url" {
  description = "URL of queue_arn, for senders."
  value       = aws_sqs_queue.gate.id
}

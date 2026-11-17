output "managed_subscription_names" {
  value       = sort(keys(local.flows))
  description = "Push subscriptions managed by this module."
}

output "managed_scheduler_job_names" {
  value       = sort(keys(local.schedules))
  description = "Scheduler names; jobs remain paused until an approved operational change."
}

output "existing_topics" {
  value       = sort(tolist(local.topic_names))
  description = "Topics referenced as data (not created or deleted by this module)."
}

output "probe_function_name" {
  value = aws_lambda_function.probe.function_name
}

output "tenant_vendors_domain_list_id" {
  value = module.network.tenant_vendors_domain_list_id
}

output "query_log_group" {
  value = "/aws/route53resolver/thor-poc"
}
